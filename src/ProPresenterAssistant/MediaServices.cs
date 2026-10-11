using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using SharpCompress.Archives;

namespace ProPresenterAssistant;

public sealed class MediaServices : IDisposable
{
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".heic", ".heif", ".bmp", ".gif", ".webp" };
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".wmv", ".webm", ".mpg", ".mpeg", ".mts", ".m2ts", ".ts", ".flv", ".vob", ".3gp", ".ogv" };
    private static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz" };
    public const long MaxEntryBytes = 8L * 1024 * 1024 * 1024;
    public const long MaxArchiveBytes = 32L * 1024 * 1024 * 1024;
    public string Cache { get; } = Path.Combine(Settings.DataRoot, "cache", Guid.NewGuid().ToString("N"));
    public List<string> Warnings { get; } = [];
    private long extractedBytes;
    private readonly Dictionary<string, string> expandedArchives = new(StringComparer.OrdinalIgnoreCase);
    public MediaServices() => Directory.CreateDirectory(Cache);
    public static bool SafeEntry(string key)
    {
        key = key.Replace('\\', '/');
        return !string.IsNullOrWhiteSpace(key) && !key.StartsWith('/') && !key.Contains(':') && !key.Split('/').Any(p => p == "..") && key.Length < 1024;
    }
    public static bool IsImage(string path) => Images.Contains(Path.GetExtension(path));
    public List<MediaItem> Scan(IEnumerable<string> inputs, IProgress<WorkProgress> progress, CancellationToken ct, Func<string, bool>? includeFile = null)
    {
        Warnings.Clear();
        var found = new Dictionary<string, MediaItem>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void FileScan(string file)
        {
            ct.ThrowIfCancellationRequested();
            if (includeFile != null && !includeFile(file)) return;
            if (found.Count >= 10000) throw new IOException("This selection contains over 10,000 media files. Choose a smaller folder.");
            try
            {
                if ((File.GetAttributes(file) & (FileAttributes.Hidden | FileAttributes.ReparsePoint)) != 0) return;
                var ext = Path.GetExtension(file);
                if (Images.Contains(ext) || Videos.Contains(ext))
                {
                    var item = new MediaItem { Source = file, Name = Path.GetFileName(file), Group = Path.GetDirectoryName(file)!, IsVideo = Videos.Contains(ext), Size = new FileInfo(file).Length };
                    found.TryAdd(item.Key, item);
                }
                else if (Archives.Contains(ext))
                {
                    using var archive = OpenArchive(file, ct);
                    long total = 0; int entries = 0;
                    foreach (var entry in archive.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (++entries > 10000) throw new IOException("Archive contains too many entries.");
                        if (entry.IsDirectory) continue;
                        string key = entry.Key ?? "";
                        if (entry.IsEncrypted || !SafeEntry(key)) { Warnings.Add($"Skipped unsafe or encrypted entry in {Path.GetFileName(file)}: {key}"); continue; }
                        if (entry.Size > MaxEntryBytes || entry.Size < 0 || total > MaxArchiveBytes - entry.Size) throw new IOException("Archive exceeds extraction limits.");
                        total += entry.Size;
                        string entryExt = Path.GetExtension(key);
                        if (!Images.Contains(entryExt) && !Videos.Contains(entryExt)) continue;
                        var item = new MediaItem { Source = file, Entry = key, Name = Path.GetFileName(key.Replace('\\', '/')), Group = file, IsVideo = Videos.Contains(entryExt), Size = entry.Size };
                        found.TryAdd(item.Key, item);
                        if (found.Count >= 10000) throw new IOException("Too many media entries.");
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Warnings.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
        }
        void Walk(string directory, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > 32 || !visited.Add(Path.GetFullPath(directory))) return;
            try
            {
                if ((File.GetAttributes(directory) & (FileAttributes.Hidden | FileAttributes.ReparsePoint)) != 0) return;
                progress.Report(new($"Searching {directory}", -1));
                foreach (string file in Directory.EnumerateFiles(directory)) FileScan(file);
                foreach (string child in Directory.EnumerateDirectories(directory)) Walk(child, depth + 1);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Warnings.Add($"{directory}: {ex.Message}"); }
        }
        foreach (string raw in inputs)
        {
            string input = Path.GetFullPath(raw);
            if (Directory.Exists(input)) Walk(input, 0); else if (File.Exists(input)) FileScan(input); else Warnings.Add($"Source unavailable: {input}");
        }
        return found.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
    }
    public async Task<string> Stage(MediaItem item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (item.Entry == null) return item.Source;
        if (item.PreviewFile != null && File.Exists(item.PreviewFile)) return item.PreviewFile;
        if (!SafeEntry(item.Entry)) throw new IOException("Unsafe archive entry.");
        string target = Path.Combine(Cache, Guid.NewGuid().ToString("N") + Path.GetExtension(item.Name));
        using var archive = OpenArchive(item.Source, ct);
        var entry = archive.Entries.FirstOrDefault(e => e.Key == item.Entry) ?? throw new IOException("Archive entry no longer exists.");
        if (entry.IsEncrypted || entry.Size > MaxEntryBytes) throw new IOException("Encrypted or oversized archive entry.");
        EnsureSpace(Cache, entry.Size);
        try
        {
            using var source = entry.OpenEntryStream();
            using var dest = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            byte[] buffer = new byte[81920]; long written = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, ct)) != 0)
            {
                written += n;
                if (written > MaxEntryBytes || Interlocked.Add(ref extractedBytes, n) > MaxArchiveBytes) throw new IOException("Extraction limit exceeded.");
                await dest.WriteAsync(buffer.AsMemory(0, n), ct);
            }
            item.PreviewFile = target;
            return target;
        }
        catch { File.Delete(target); throw; }
    }
    public static void EnsureSpace(string directory, long bytes)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!);
        if (drive.AvailableFreeSpace < bytes + 128 * 1024 * 1024) throw new IOException("Not enough free disk space. Free some space and retry.");
    }
    private IArchive OpenArchive(string path, CancellationToken ct)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not ".gz" and not ".tgz") return ArchiveFactory.OpenArchive(path);
        if (!expandedArchives.TryGetValue(path, out string? expanded))
        {
            expanded = Path.Combine(Cache, Guid.NewGuid().ToString("N") + ".tar");
            try
            {
                using var input = File.OpenRead(path); using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
                using var output = File.Create(expanded); byte[] buffer = new byte[81920]; long total = 0; int n;
                while ((n = gzip.Read(buffer, 0, buffer.Length)) > 0) { ct.ThrowIfCancellationRequested(); total += n; if (total > MaxArchiveBytes || Interlocked.Add(ref extractedBytes, n) > MaxArchiveBytes) throw new IOException("Compressed TAR exceeds extraction limits."); EnsureSpace(Cache, n); output.Write(buffer, 0, n); }
                expandedArchives[path] = expanded;
            }
            catch { File.Delete(expanded); throw; }
        }
        return ArchiveFactory.OpenArchive(expanded);
    }
    public async Task<(BitmapSource? Image, string Detail)> Thumbnail(MediaItem item, CancellationToken ct)
    {
        string source = await Stage(item, ct);
        if (!item.IsVideo)
        {
            try { return (ReadImage(source, 320), FormatSize(item.Size)); } catch { }
        }
        var probe = await Probe(source, ct);
        string png = Path.Combine(Cache, Guid.NewGuid().ToString("N") + ".png");
        await RunTool("ffmpeg", ["-v", "error", "-threads", "2", "-i", source, "-frames:v", "1", "-vf", "scale=320:-1", "-y", png], ct);
        string detail = item.IsVideo ? $"{TimeSpan.FromSeconds(probe.Duration):m\\:ss} • {probe.Width} × {probe.Height}" : FormatSize(item.Size);
        return (ReadImage(png, 320), detail);
    }
    public static BitmapSource ReadImage(string path, int decodeWidth = 0)
    {
        using var stream = File.OpenRead(path);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
    private static string FormatSize(long size) => $"{size / 1048576.0:0.0} MB";
    public record ProbeInfo(int Width, int Height, double Duration, double FrameRate, string Codec, string PixelFormat, string AudioCodec);
    public static async Task<ProbeInfo> Probe(string path, CancellationToken ct)
    {
        string output = await RunTool("ffprobe", ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], ct);
        using var doc = JsonDocument.Parse(output);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "video");
        if (video.ValueKind == JsonValueKind.Undefined) throw new IOException("No playable image or video stream.");
        string Text(JsonElement e, string key) => e.TryGetProperty(key, out var v) ? v.ToString() : "";
        double duration = double.TryParse(Text(doc.RootElement.GetProperty("format"), "duration"), CultureInfo.InvariantCulture, out var d) ? d : 0;
        string[] rate = Text(video, "avg_frame_rate").Split('/');
        double fps = rate.Length == 2 && double.TryParse(rate[0], out var a) && double.TryParse(rate[1], out var b) && b != 0 ? a / b : 30;
        var audio = streams.FirstOrDefault(s => Text(s, "codec_type") == "audio");
        return new(video.GetProperty("width").GetInt32(), video.GetProperty("height").GetInt32(), duration, fps,
            Text(video, "codec_name"), Text(video, "pix_fmt"), audio.ValueKind == JsonValueKind.Undefined ? "" : Text(audio, "codec_name"));
    }
    public async Task<PreparedMedia> Prepare(MediaItem item, string folder, int index, IProgress<WorkProgress> progress, CancellationToken ct)
    {
        string source = await Stage(item, ct);
        string baseName = $"{index + 1:0000}-{CleanName(Path.GetFileNameWithoutExtension(item.Name))}";
        string ext = Path.GetExtension(item.Name).ToLowerInvariant();
        string dest = Path.Combine(folder, baseName + ext);
        EnsureSpace(folder, new FileInfo(source).Length);
        progress.Report(new($"Copying {item.Name}", 0));
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
        using (var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
        {
            byte[] buffer = new byte[81920]; long total = 0; int n;
            while ((n = await input.ReadAsync(buffer, ct)) > 0) { await output.WriteAsync(buffer.AsMemory(0, n), ct); total += n; progress.Report(new($"Copying {item.Name}", input.Length == 0 ? 1 : total / (double)input.Length)); }
        }
        if (!item.IsVideo)
        {
            // Normalize still images to RGB PNG for a stable import across codecs.
            string png = Path.Combine(folder, baseName + "-rgb.png");
            await RunTool("ffmpeg", ["-v", "error", "-threads", "2", "-i", dest, "-frames:v", "1", "-vf", "format=rgba", "-y", png], ct);
            File.Delete(dest); var image = ReadImage(png);
            return new(png, item.Name, false, image.PixelWidth, image.PixelHeight, 0, 0);
        }
        var p = await Probe(dest, ct);
        bool container = ext is ".mp4" or ".mov";
        bool supported = p.Codec is "h264" or "hevc" or "prores" or "hap";
        bool audioSupported = p.AudioCodec is "" or "aac" or "mp3" || p.AudioCodec.StartsWith("pcm_");
        if (!container || !supported || !audioSupported)
        {
            bool alpha = p.PixelFormat.StartsWith("yuva") || p.PixelFormat.StartsWith("rgba") || p.PixelFormat.StartsWith("bgra") || p.PixelFormat.StartsWith("argb") || p.PixelFormat.StartsWith("abgr") || p.PixelFormat.StartsWith("gbrap") || p.PixelFormat.StartsWith("ya");
            string converted = Path.Combine(folder, baseName + "-converted" + (alpha ? ".mov" : ".mp4"));
            EnsureSpace(folder, (long)Math.Max(new FileInfo(dest).Length * 2, p.Duration * (alpha ? 50000000 : 6000000)));
            List<string> args = ["-v", "error", "-threads", "2", "-i", dest, "-map", "0:v:0", "-map", "0:a:0?", "-c:v", alpha ? "prores_ks" : "libx264"];
            args.AddRange(["-threads", "2"]);
            args.AddRange(alpha ? ["-profile:v", "4", "-pix_fmt", "yuva444p10le"] : ["-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p", "-vf", "scale=ceil(iw/2)*2:ceil(ih/2)*2", "-movflags", "+faststart"]);
            args.AddRange(["-c:a", "aac", "-b:a", "192k", "-progress", "pipe:1", "-nostats", "-y", converted]);
            await RunTool("ffmpeg", args, ct, line =>
            {
                if (line.StartsWith("out_time_us=") && long.TryParse(line[12..], out var us))
                    progress.Report(new($"Converting {item.Name}", p.Duration > 0 ? Math.Clamp(us / 1000000.0 / p.Duration, 0, 1) : -1));
            });
            p = await Probe(converted, ct); File.Delete(dest); dest = converted;
        }
        return new(dest, item.Name, true, p.Width, p.Height, p.Duration, p.FrameRate);
    }
    public static string CleanName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string clean = new(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray());
        clean = clean.Trim().TrimEnd('.');
        if (clean.Length > 100) clean = clean[..100];
        return string.IsNullOrEmpty(clean) ? "Imported media" : clean;
    }
    public static string ToolPath(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "tools", name + ".exe");
        if (File.Exists(path)) return path;
        var assembly = typeof(MediaServices).Assembly;
        using var resource = assembly.GetManifestResourceStream("tools." + name + ".exe") ?? throw new FileNotFoundException($"The bundled {name} tool is missing. Reinstall ProPresenter Assistant.");
        string cache = Path.Combine(Settings.DataRoot, "tools", assembly.GetName().Version?.ToString() ?? "0.1"); Directory.CreateDirectory(cache);
        path = Path.Combine(cache, name + ".exe");
        if (!File.Exists(path))
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { using (var output = File.Create(temp)) resource.CopyTo(output); File.Move(temp, path, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        return path;
    }
    public static async Task<string> RunTool(string name, IEnumerable<string> args, CancellationToken ct, Action<string>? lineHandler = null)
    {
        var start = new ProcessStartInfo(ToolPath(name)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {name}.");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        using var reg = ct.Register(() => { try { process.Kill(true); } catch { } });
        var errors = process.StandardError.ReadToEndAsync(ct);
        var output = new System.Text.StringBuilder(); string? line;
        while ((line = await process.StandardOutput.ReadLineAsync(ct)) != null) { output.AppendLine(line); lineHandler?.Invoke(line); }
        await process.WaitForExitAsync(ct);
        string error = await errors;
        if (process.ExitCode != 0) throw new IOException($"{name}: {error[..Math.Min(1200, error.Length)]}");
        return output.ToString();
    }
    public void Dispose() { try { if (Directory.Exists(Cache)) Directory.Delete(Cache, true); } catch { } }
}
