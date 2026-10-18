using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using ProPresenterAssistant.Updates;

namespace ProPresenterAssistant;

public static class UpdateTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
    public static async Task Run(Action<bool, string> assert, string root)
    {
        var schedule = new UpdateSchedule(); var now = DateTimeOffset.Parse("2026-10-18T12:00:00Z");
        assert(schedule.Due(now), "Update check is due on every fresh application launch");
        schedule.Succeeded(now);
        assert(!schedule.Due(now.AddHours(23.99)) && schedule.Due(now.AddDays(1)) && schedule.Due(now.AddDays(2)), "Daily update deadline catches up after sleep without checking early");
        schedule.Failed(now);
        assert(!schedule.Due(now.AddMinutes(4)) && schedule.Due(now.AddMinutes(5)), "Offline or failed checks retry after five minutes");
        assert(UpdateSafety.ParseVersion("v0.10.0") > UpdateSafety.ParseVersion("v0.2.0") && UpdateSafety.ParseVersion("v0.3.0-beta") == null, "Release comparisons use numeric stable versions");
        byte[] bytes = "verified installer fixture"u8.ToArray();
        string digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        object Data(string tag = "v99.0.0", string url = "https://github.com/alantoewsio/propresenter-assistant/releases/download/v99.0.0/ProPresenterAssistant-Setup.exe", string? hash = null, bool prerelease = false, bool draft = false) => new { tag_name = tag, prerelease, draft, body = "Release notes", assets = new[] { new { name = "ProPresenterAssistant-Setup.exe", browser_download_url = url, size = bytes.Length, digest = hash ?? digest } } };
        UpdateRelease? Parse(object value) { using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value)); return UpdateClient.Parse(doc.RootElement, new Version(0, 2, 0)); }
        assert(Parse(Data("v0.2.0")) == null && Parse(Data("v0.1.0")) == null && Parse(Data(prerelease: true)) == null && Parse(Data(draft: true)) == null, "Updater ignores equal, older, draft and prerelease builds");
        bool rejected = false; try { Parse(Data(url: "https://evil.example/installer.exe")); } catch (IOException) { rejected = true; }
        assert(rejected, "Updater refuses installer assets outside the project's GitHub releases");
        rejected = false; try { Parse(Data(hash: "")); } catch (IOException) { rejected = true; }
        assert(rejected, "Updater refuses a release with no valid checksum");
        using var http = new HttpClient(new Handler(request => request.RequestUri!.Host == "api.github.com"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(Data())) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var client = new UpdateClient(http); var release = (await client.Check(CancellationToken.None))!;
        string download = await client.Download(release, new Progress<WorkProgress>(), CancellationToken.None);
        try { assert(File.ReadAllBytes(download).SequenceEqual(bytes), "GitHub release discovery and verified download work through HTTP responses"); }
        finally { Directory.Delete(Path.GetDirectoryName(download)!, true); }
        using var corrupt = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[bytes.Length]) }));
        rejected = false; try { await new UpdateClient(corrupt).Download(release, new Progress<WorkProgress>(), CancellationToken.None); } catch (IOException) { rejected = true; }
        assert(rejected, "Checksum mismatch prevents a downloaded installer from being accepted");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); rejected = false;
        try { await client.Download(release, new Progress<WorkProgress>(), cancelled.Token); } catch (OperationCanceledException) { rejected = true; }
        assert(rejected, "User cancellation stops an update download before handoff");
        string stage = Path.Combine(root, "update-stage"), target = Path.Combine(root, "update-target"), backup = Path.Combine(root, "update-backup");
        Directory.CreateDirectory(stage); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(stage, "app.exe"), "new"); File.WriteAllText(Path.Combine(stage, "new.dll"), "added");
        File.WriteAllText(Path.Combine(target, "app.exe"), "old"); File.WriteAllText(Path.Combine(target, "user-settings.json"), "keep");
        var transaction = new InstallTransaction(stage, target, backup); transaction.Apply();
        assert(File.ReadAllText(Path.Combine(target, "app.exe")) == "new" && File.ReadAllText(Path.Combine(target, "user-settings.json")) == "keep", "Installer transaction replaces payload files while preserving unrelated user files");
        transaction.Rollback();
        assert(File.ReadAllText(Path.Combine(target, "app.exe")) == "old" && !File.Exists(Path.Combine(target, "new.dll")), "Failed update rollback restores previous files and removes newly installed files");
        string locked = Path.Combine(target, "unchanged.dll"); File.WriteAllText(locked, "same"); File.WriteAllText(Path.Combine(stage, "unchanged.dll"), "same");
        using (var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read)) new InstallTransaction(stage, target, Path.Combine(root, "update-backup2")).Apply();
        assert(File.ReadAllText(locked) == "same", "Unchanged locked Explorer files are skipped during installation");
        rejected = false; try { UpdateSafety.SafePath(stage, "../escape.exe"); } catch (IOException) { rejected = true; }
        assert(rejected, "Update payload paths cannot escape the staging folder");
        using (var gate = UpdateSafety.AcquireLock()) assert(UpdateSafety.Installing(), "Installation lock prevents concurrent updates and ordinary assistant startups");
        assert(!UpdateSafety.Installing(), "Installation lock is released after the installer exits");
    }
}
