using System.IO;
using System.Text.Json.Nodes;
namespace ProPresenterAssistant;
public static class LegacyMediaTests
{
    public static (string Folder, JsonArray Items) Create(string root, bool duplicate = false)
    {
        string folder = Path.Combine(root, "Legacy show"); Directory.CreateDirectory(Path.Combine(folder, "Playlists"));
        Directory.CreateDirectory(Path.Combine(folder, "Libraries", "Default"));
        Proto Id(string value) => new Proto().Text(1, value);
        Proto Cue(string cue, string media, string path) => new Proto().Message(1, Id(cue)).Text(2, "Same name")
            .Message(10, new Proto().Message(20, new Proto().Message(5, new Proto().Message(1, Id(media)).Message(2, new Proto().Text(1, path)))));
        Proto Item(Proto cue) => new Proto().Message(5, cue);
        string first = Path.Combine(folder, "First", "same.png"), second = Path.Combine(folder, "Second", "same.png");
        var source = new Proto().Message(1, Id("playlist")).Message(13, new Proto()
            .Message(1, Item(Cue("source-cue-a", "source-object-a", first)))
            .Message(1, Item(Cue("source-cue-b", "source-object-b", second))));
        var bin = new Proto().Message(1, Id("bin")).Message(13, new Proto()
            .Message(1, Item(Cue("bin-cue-a", "bin-object-a", first)))
            .Message(1, Item(Cue("bin-cue-b", "bin-object-b", second))));
        if (duplicate) bin.Message(8, Cue("duplicate-bin-cue", "duplicate-bin-object", first));
        byte[] Document(Proto child) => new Proto().Message(3, new Proto().Message(12, new Proto().Message(1, child))).ToArray();
        File.WriteAllBytes(Path.Combine(folder, "Playlists", "Library"), Document(source));
        File.WriteAllBytes(Path.Combine(folder, "Playlists", "Media"), Document(bin));
        return (folder, JsonNode.Parse("[{\"id\":{\"uuid\":\"source-object-a\",\"name\":\"Same name\",\"index\":0},\"type\":\"media\",\"is_hidden\":false,\"is_pco\":false,\"duration\":5},{\"id\":{\"uuid\":\"source-object-b\",\"name\":\"Same name\",\"index\":1},\"type\":\"media\",\"is_hidden\":true,\"is_pco\":false,\"duration\":8}]")!.AsArray());
    }
    public static void Run(Action<bool, string> assert, string root)
    {
        var fixture = Create(root); var map = LegacyMediaMap.Read(Path.Combine(fixture.Folder, "Playlists", "Library"), Path.Combine(fixture.Folder, "Playlists", "Media"), "playlist");
        var normalized = map.Normalize(fixture.Items, false);
        assert(normalized[0]!["target_uuid"]!.ToString() == "bin-cue-a" && normalized[1]!["target_uuid"]!.ToString() == "bin-cue-b" && fixture.Items[0]!["target_uuid"] == null, "Legacy media resolves exact stored paths, distinguishes duplicate names and leaves API snapshot unchanged");
        var after = fixture.Items.DeepClone().AsArray(); after[0]!["id"]!["uuid"] = "bin-object-a"; after[1]!["id"]!["uuid"] = "bin-object-b";
        var verified = map.Normalize(after, true);
        assert(normalized.Zip(verified).All(pair => ProPresenterClient.SamePlaylistItem(pair.First, pair.Second)), "Legacy media verification compares real targets despite regenerated media-object IDs and preserves durations and hidden flags");
        after[0]!["id"]!["uuid"] = "unknown"; bool refused = false;
        try { map.Normalize(after, true); } catch (IOException) { refused = true; }
        assert(refused, "Legacy verification refuses unknown media objects instead of matching names");
        string file = Path.Combine(fixture.Folder, "Playlists", "Media"); File.WriteAllBytes(file, [26, 255]); refused = false;
        try { LegacyMediaMap.Read(Path.Combine(fixture.Folder, "Playlists", "Library"), file, "playlist"); } catch (IOException) { refused = true; }
        assert(refused, "Malformed native playlist metadata fails closed before writes");
        fixture = Create(root, true); refused = false;
        try { LegacyMediaMap.Read(Path.Combine(fixture.Folder, "Playlists", "Library"), file, "playlist"); } catch (IOException) { refused = true; }
        assert(refused, "Multiple media-bin entries for the same exact source path fail closed instead of choosing arbitrarily");
        fixture = Create(root); map = LegacyMediaMap.Read(Path.Combine(fixture.Folder, "Playlists", "Library"), file, "playlist"); map.EnsureUnchanged();
        File.WriteAllBytes(file, new Proto().Message(3, new Proto()).ToArray()); refused = false;
        try { map.EnsureUnchanged(); } catch (IOException) { refused = true; }
        assert(refused, "Native media metadata edits are detected before a playlist update");
    }
}
