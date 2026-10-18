using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using ProPresenterAssistant.Updates;

namespace ProPresenterAssistant;

public record UpdateRelease(string Version, string Notes, Uri Download, long Size, string Digest);
public sealed class UpdateSchedule
{
    public DateTimeOffset Next { get; private set; } = DateTimeOffset.MinValue;
    public bool Due(DateTimeOffset now) => now >= Next;
    public void Succeeded(DateTimeOffset now) => Next = now.AddDays(1);
    public void Failed(DateTimeOffset now) => Next = now.AddMinutes(5);
}
public sealed class UpdateClient(HttpClient http)
{
    public const string Endpoint = "https://api.github.com/repos/alantoewsio/propresenter-assistant/releases/latest";
    public static Version Current => UpdateSafety.ParseVersion(typeof(App).Assembly.GetName().Version!.ToString(3))!;
    public static UpdateRelease? Parse(JsonElement release, Version installed)
    {
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        var version = UpdateSafety.ParseVersion(tag); if (version == null || version <= installed) return null;
        var assets = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == "ProPresenterAssistant-Setup.exe").ToArray();
        if (assets.Length != 1) throw new IOException("The latest release has no unique Windows installer.");
        var asset = assets[0]; string digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
        if (digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest[7..].Any(c => !Uri.IsHexDigit(c))) throw new IOException("The release has no valid installer checksum.");
        if (!Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var url) || url.Scheme != "https" || url.Host != "github.com" || !url.AbsolutePath.StartsWith("/alantoewsio/propresenter-assistant/releases/download/", StringComparison.Ordinal) || !string.IsNullOrEmpty(url.UserInfo)) throw new IOException("Unexpected update download address.");
        long size = asset.GetProperty("size").GetInt64(); if (size <= 0 || size > 1024L * 1024 * 1024) throw new IOException("Unexpected installer size.");
        return new(version.ToString(3), release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "", url, size, digest);
    }
    public async Task<UpdateRelease?> Check(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.UserAgent.ParseAdd("ProPresenterAssistant/" + Current.ToString(3));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return Parse(json.RootElement, Current);
    }
    public async Task<string> Download(UpdateRelease release, IProgress<WorkProgress> progress, CancellationToken ct)
    {
        string folder = Path.Combine(UpdateSafety.Root, "download-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string part = Path.Combine(folder, "installer.part"), path = Path.Combine(folder, "ProPresenterAssistant-Setup.exe");
        try
        {
            using var response = await http.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != release.Size) throw new IOException("Unexpected download size.");
            using (var source = await response.Content.ReadAsStreamAsync(ct))
            using (var target = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                byte[] buffer = new byte[81920]; long count = 0; int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    count += read; if (count > release.Size) throw new IOException("The download exceeds its expected size.");
                    await target.WriteAsync(buffer.AsMemory(0, read), ct); progress.Report(new("Downloading update… " + (count * 100 / release.Size) + "%", (double)count / release.Size));
                }
            }
            progress.Report(new("Verifying downloaded installer…", -1)); await UpdateSafety.Verify(part, release.Size, release.Digest, ct); File.Move(part, path); return path;
        }
        catch { try { Directory.Delete(folder, true); } catch { } throw; }
    }
}
