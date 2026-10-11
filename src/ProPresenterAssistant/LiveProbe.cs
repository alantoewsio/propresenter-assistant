using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace ProPresenterAssistant;
public static class LiveProbe
{
    // Integration fixture only: no cue triggering or output changes.
    public static async Task<int> Run(int port, string libraryFolder, string report)
    {
        var checks = new List<string>();
        var settings = Settings.Load(); settings.Port = port;
        string name = "Assistant Import Test " + DateTime.Now.ToString("HHmmss");
        var job = new ImportJob { Name = name, Folder = Path.Combine(settings.MediaRoot, name), Count = 3 };
        Directory.CreateDirectory(job.Folder); job.PresentationFile = Path.Combine(job.Folder, name + ".pro");
        try
        {
            List<PreparedMedia> prepared = [];
            foreach (var (file, color) in new[] { ("01-purple.png", "purple"), ("02-blue.png", "blue") })
            {
                string path = Path.Combine(job.Folder, file); await MediaServices.RunTool("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", $"color=c={color}:s=640x360", "-frames:v", "1", "-y", path], CancellationToken.None); prepared.Add(new(path, file, false, 640, 360, 0, 0));
            }
            string movie = Path.Combine(job.Folder, "03-video.mp4"); await MediaServices.RunTool("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=24", "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-y", movie], CancellationToken.None); prepared.Add(new(movie, "03-video.mp4", true, 640, 360, 2, 24));
            File.WriteAllBytes(job.PresentationFile, NativePresentation.Create(name, job.PresentationId, prepared));
            if (!Directory.Exists(libraryFolder)) throw new IOException("Test library does not exist.");
            using var client = new ProPresenterClient(settings);
            string? resolved = await client.FindLibraryFolder(CancellationToken.None);
            if (!string.Equals(resolved, libraryFolder, StringComparison.OrdinalIgnoreCase)) throw new IOException("Automatic library discovery did not match the expected library.");
            checks.Add("Default library discovered through the live API");
            await client.ImportPresentation(job, CancellationToken.None); checks.Add("Native presentation imported through the app’s library workflow");
            bool detected = false;
            for (int i = 0; i < 20; i++) { if (await client.PresentationReady(job.PresentationId, 3, CancellationToken.None)) { detected = true; break; } await Task.Delay(500); }
            if (!detected) throw new IOException("Library did not index the presentation automatically. Prepared fixture retained for native import.");
            checks.Add("ProPresenter indexed presentation with three slides");
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            using var response = await http.PostAsJsonAsync("v1/playlists", new { name, type = "playlist" }); response.EnsureSuccessStatusCode();
            var created = JsonNode.Parse(await response.Content.ReadAsStringAsync()); job.PlaylistId = created?["id"]?["uuid"]?.ToString() ?? "";
            if (job.PlaylistId == "") job.PlaylistId = (await client.Playlists(CancellationToken.None)).Single(p => p.Name == name).Id;
            checks.Add("Dedicated test playlist created");
            var other = (await client.Playlists(CancellationToken.None)).FirstOrDefault(p => p.Id != job.PlaylistId && p.Name == "Sample Playlist");
            if (other != null)
            {
                var original = (await client.Get("v1/playlist/" + other.Id, CancellationToken.None))["items"]!.AsArray();
                var seed = new JsonArray();
                var existing = original.FirstOrDefault(n => n?["type"]?.ToString() == "presentation");
                seed.Add(new JsonObject { ["id"] = new JsonObject { ["uuid"] = Guid.NewGuid().ToString(), ["name"] = "Existing test header", ["index"] = 0 }, ["type"] = "header", ["is_hidden"] = false, ["is_pco"] = false, ["destination"] = "presentation", ["header_color"] = new JsonObject { ["red"] = 0.3, ["green"] = 0.4, ["blue"] = 0.6, ["alpha"] = 1.0 } });
                if (existing != null) seed.Add(existing.DeepClone());
                using var seeded = await http.PutAsJsonAsync("v1/playlist/" + job.PlaylistId, ProPresenterClient.WriteItems(seed));
                if (!seeded.IsSuccessStatusCode) throw new IOException(await seeded.Content.ReadAsStringAsync());
                checks.Add("Existing header and presentation seeded in dedicated test playlist");
            }
            await client.Complete(job, new Progress<WorkProgress>(), CancellationToken.None, false); checks.Add("Native presentation linked and verified in test playlist");
            await client.Complete(job, new Progress<WorkProgress>(), CancellationToken.None, false); checks.Add("Repeated completion does not add a duplicate entry");
            var details = await client.Get("v1/presentation/" + job.PresentationId, CancellationToken.None); File.WriteAllText(Path.ChangeExtension(report, ".presentation.json"), details.ToJsonString());
            File.WriteAllText(report, JsonSerializer.Serialize(new { passed = true, checks, job }, new JsonSerializerOptions { WriteIndented = true })); return 0;
        }
        catch (Exception ex) { File.WriteAllText(report, JsonSerializer.Serialize(new { passed = false, checks, job, error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true })); return 1; }
    }
}
