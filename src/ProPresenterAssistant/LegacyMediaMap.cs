using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

namespace ProPresenterAssistant;

// Read-only subset of PlaylistDocument's protobuf layout, checked against 17.1
// and 21.4.2. No vendor assemblies or customer playlist files are distributed.
public sealed class LegacyMediaMap
{
    public record Target(string CueId, string MediaId, string Path);
    private readonly Dictionary<string, Target> targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> mediaIds = new(StringComparer.OrdinalIgnoreCase);
    private string libraryFile = "", mediaFile = "";
    private byte[] libraryHash = [], mediaHash = [];
    public IReadOnlyCollection<Target> Targets => targets.Values.ToArray();
    public static LegacyMediaMap Read(string libraryFile, string mediaFile, string playlistId)
    {
        byte[] libraryBytes = ReadFile(libraryFile), mediaBytes = ReadFile(mediaFile);
        var library = Playlists(libraryBytes).Where(p => Id(p) == playlistId.ToLowerInvariant()).ToArray();
        if (library.Length != 1) throw new IOException("Cannot uniquely locate the selected playlist in ProPresenter's local playlist file.");
        var bin = Playlists(mediaBytes).SelectMany(Cues).Select(Media).Where(x => x != null).Cast<Target>().ToArray();
        var map = new LegacyMediaMap { libraryFile = libraryFile, mediaFile = mediaFile, libraryHash = SHA256.HashData(libraryBytes), mediaHash = SHA256.HashData(mediaBytes) };
        foreach (var item in Cues(library[0]))
        {
            var source = Media(item); if (source == null) continue;
            var matches = bin.Where(x => string.Equals(x.Path, source.Path, StringComparison.OrdinalIgnoreCase)).GroupBy(x => x.CueId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
            if (matches.Length != 1) throw new IOException("An existing media item has no unique media-bin reference for its exact source path. No playlist update was sent.");
            if (!map.targets.TryAdd(source.MediaId, matches[0]) && map.targets[source.MediaId] != matches[0]) throw new IOException("Conflicting native media references. No update was sent.");
        }
        foreach (var target in map.targets.Values)
        {
            if (map.mediaIds.TryGetValue(target.MediaId, out var previous) && previous != target.CueId) throw new IOException("Ambiguous media-object identifier. No update was sent.");
            map.mediaIds[target.MediaId] = target.CueId;
        }
        return map;
    }
    public void EnsureUnchanged()
    {
        if (!libraryHash.SequenceEqual(SHA256.HashData(ReadFile(libraryFile))) || !mediaHash.SequenceEqual(SHA256.HashData(ReadFile(mediaFile))))
            throw new IOException("ProPresenter’s stored playlist or media bin changed while importing. No update was sent. Use Resume import after editing finishes.");
    }
    public JsonArray Normalize(JsonArray items, bool after)
    {
        var copy = items.DeepClone().AsArray();
        foreach (var node in copy.OfType<JsonObject>().Where(x => x["type"]?.ToString() == "media" && string.IsNullOrWhiteSpace(x["target_uuid"]?.ToString())))
        {
            string id = node["id"]?["uuid"]?.ToString() ?? "";
            string? target = after ? mediaIds.GetValueOrDefault(id) : targets.GetValueOrDefault(id)?.CueId;
            if (target == null) throw new IOException("The live media entry does not match the stored native media references. No further playlist update was sent.");
            node["target_uuid"] = target;
        }
        return copy;
    }
    private static byte[] ReadFile(string path)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new IOException("Playlist metadata exceeds the read limit.");
        return File.ReadAllBytes(path);
    }
    private static IEnumerable<byte[]> Playlists(byte[] document)
    {
        var root = Fields(document).FirstOrDefault(f => f.Number == 3 && f.Wire == 2)?.Bytes ?? throw new IOException("Unsupported native playlist document.");
        return Walk(root, 0);
        static IEnumerable<byte[]> Walk(byte[] playlist, int depth)
        {
            if (depth > 32) throw new IOException("Playlist nesting exceeds the read limit.");
            yield return playlist;
            foreach (var field in Fields(playlist))
            {
                if (field.Number == 9 && field.Wire == 2) foreach (var child in Walk(field.Bytes, depth + 1)) yield return child;
                if (field.Number == 12 && field.Wire == 2)
                    foreach (var entry in Fields(field.Bytes).Where(f => f.Number == 1 && f.Wire == 2))
                        foreach (var child in Walk(entry.Bytes, depth + 1)) yield return child;
            }
        }
    }
    private static string Id(byte[] bytes) => Text(Message(bytes, 1), 1).ToLowerInvariant();
    private static IEnumerable<byte[]> Cues(byte[] playlist)
    {
        foreach (var f in Fields(playlist))
        {
            if (f.Number == 8 && f.Wire == 2) yield return f.Bytes;
            if (f.Number == 13 && f.Wire == 2)
                foreach (var item in Fields(f.Bytes).Where(x => x.Number == 1 && x.Wire == 2))
                {
                    byte[] cue = Message(item.Bytes, 5); if (cue.Length > 0) yield return cue;
                }
        }
    }
    private static Target? Media(byte[] cue)
    {
        var media = Fields(cue).Where(f => f.Number == 10 && f.Wire == 2).Select(f => Message(Message(f.Bytes, 20), 5)).Where(b => b.Length > 0).ToArray();
        if (media.Length == 0) return null;
        if (media.Length != 1) throw new IOException("A legacy media cue contains multiple media actions and cannot be resolved safely.");
        string path = Text(Message(media[0], 2), 1);
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile) path = uri.LocalPath;
        if (!Path.IsPathFullyQualified(path)) throw new IOException("Legacy media has no absolute source path.");
        path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        string cueId = Id(cue), mediaId = Id(media[0]);
        if (string.IsNullOrWhiteSpace(cueId) || string.IsNullOrWhiteSpace(mediaId)) throw new IOException("Legacy media metadata has an empty identifier.");
        return new(cueId, mediaId, path);
    }
    private static byte[] Message(byte[] bytes, int field) => Fields(bytes).FirstOrDefault(f => f.Number == field && f.Wire == 2)?.Bytes ?? [];
    private static string Text(byte[] bytes, int field) => Encoding.UTF8.GetString(Message(bytes, field));
    private record Field(int Number, int Wire, byte[] Bytes);
    private static List<Field> Fields(byte[] bytes)
    {
        var fields = new List<Field>(); int offset = 0;
        ulong Var()
        {
            ulong value = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                if (offset >= bytes.Length) throw new IOException("Truncated playlist metadata.");
                byte b = bytes[offset++]; if (shift == 63 && b > 1) throw new IOException("Invalid playlist varint.");
                value |= (ulong)(b & 127) << shift; if (b < 128) return value;
            }
            throw new IOException("Invalid playlist varint.");
        }
        while (offset < bytes.Length)
        {
            ulong tag = Var(); if ((tag >> 3) == 0 || tag >> 3 > int.MaxValue) throw new IOException("Invalid playlist field.");
            int wire = (int)(tag & 7), size;
            if (wire == 0) { Var(); continue; }
            if (wire == 1) size = 8; else if (wire == 5) size = 4;
            else if (wire == 2) { ulong length = Var(); if (length > int.MaxValue) throw new IOException("Oversized playlist field."); size = (int)length; }
            else throw new IOException("Unsupported playlist wire type.");
            if (size > bytes.Length - offset) throw new IOException("Truncated playlist field.");
            fields.Add(new((int)(tag >> 3), wire, bytes.AsSpan(offset, size).ToArray())); offset += size;
        }
        return fields;
    }
}
