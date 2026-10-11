using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace ProPresenterAssistant;

public sealed class ProPresenterClient(Settings settings) : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private string Url(string route) => $"http://127.0.0.1:{settings.Port}/" + route;
    public async Task<JsonNode> Get(string route, CancellationToken ct)
    {
        using var response = await http.GetAsync(Url(route), ct); response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) ?? throw new IOException("Empty response from ProPresenter.");
    }
    public async Task EnsureRunning(IProgress<WorkProgress> progress, CancellationToken ct)
    {
        var status = await ApiDiscovery.Detect(settings, ct);
        if (settings.AutoDetectApi && status.ActivePort != null) settings.Port = status.ActivePort.Value;
        if (status.ActivePort != null) return;
        if (status.Running && status.Enabled == false) throw new IOException(status.Message + ". Enable Network access in ProPresenter to import.");
        if (!Process.GetProcessesByName("ProPresenter").Any())
        {
            if (!File.Exists(settings.Executable)) throw new IOException("Choose the ProPresenter executable in Settings.");
            Process.Start(new ProcessStartInfo(settings.Executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(settings.Executable)! });
        }
        progress.Report(new("Waiting for ProPresenter’s local API…", -1));
        for (int i = 0; i < 25; i++)
        {
            ct.ThrowIfCancellationRequested();
            status = await ApiDiscovery.Detect(settings, ct);
            if (settings.AutoDetectApi && status.ActivePort != null) settings.Port = status.ActivePort.Value;
            if (status.ActivePort != null) return;
            if (status.Running && status.Enabled == false) throw new IOException(status.Message + ". Enable Network access in ProPresenter to import.");
            await Task.Delay(1000, ct);
        }
        throw new IOException($"Cannot connect to ProPresenter on port {settings.Port}. Enable Network/API in ProPresenter Settings, check the port, and retry.");
    }
    public async Task<List<Playlist>> Playlists(CancellationToken ct)
    {
        var tree = await Get("v1/playlists", ct); var list = new List<Playlist>();
        void Walk(JsonArray nodes, string parent)
        {
            foreach (var node in nodes)
            {
                if (node == null) continue;
                string name = node["id"]?["name"]?.ToString() ?? "Unnamed";
                string path = string.IsNullOrEmpty(parent) ? name : parent + " / " + name;
                string id = node["id"]?["uuid"]?.ToString() ?? "";
                string kind = node["field_type"]?.ToString() ?? node["type"]?.ToString() ?? "";
                bool folder = kind is "folder" or "group" || node["is_folder"]?.GetValue<bool>() == true;
                if (!folder && id.Length > 0) list.Add(new(id, path));
                if (node["children"] is JsonArray children) Walk(children, path);
            }
        }
        Walk(tree.AsArray(), ""); return list;
    }
    public async Task<bool> PresentationReady(string id, int count, CancellationToken ct)
    {
        try
        {
            var presentation = await Get("v1/presentation/" + id.ToLowerInvariant(), ct);
            presentation = presentation["presentation"] ?? presentation;
            int slides = presentation["groups"]?.AsArray().Sum(g => g?["slides"]?.AsArray().Count ?? 0) ?? 0;
            return slides == count;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return false; }
    }
    public void OpenPresentation(string path)
    {
        // Native import through ProPresenter's command-line file-open handler.
        var start = new ProcessStartInfo(settings.Executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(settings.Executable)! };
        start.ArgumentList.Add(path); Process.Start(start);
    }
    public async Task<string?> FindLibraryFolder(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(settings.LibraryFolder))
        {
            if (!Directory.Exists(settings.LibraryFolder)) throw new IOException("The configured ProPresenter library folder is unavailable.");
            return settings.LibraryFolder;
        }
        var libraries = (await Get("v1/libraries", ct)).AsArray();
        JsonNode? Id(JsonNode? n) => n?["id"] ?? n;
        var preferred = libraries.FirstOrDefault(l => Id(l)?["name"]?.ToString() == "Default") ?? libraries.FirstOrDefault();
        string preferredName = Id(preferred)?["name"]?.ToString() ?? "";
        foreach (var library in libraries.OrderByDescending(l => l == preferred))
        {
            string uuid = Id(library)?["uuid"]?.ToString() ?? "";
            if (uuid == "") continue;
            var contents = await Get("v1/library/" + uuid, ct);
            foreach (var item in contents["items"]?.AsArray().Take(1) ?? [])
            {
                string pid = Id(item)?["uuid"]?.ToString() ?? "";
                if (pid == "") continue;
                var detail = await Get("v1/presentation/" + pid, ct); detail = detail["presentation"] ?? detail;
                string? path = detail["presentation_path"]?.ToString();
                if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)) continue;
                string? folder = Path.GetDirectoryName(path);
                if (folder == null || !Directory.Exists(folder)) continue;
                if (library == preferred) return folder;
                if (preferredName == Path.GetFileName(preferredName))
                {
                    string candidate = Path.Combine(Path.GetDirectoryName(folder)!, preferredName);
                    if (Directory.Exists(candidate)) return candidate;
                }
            }
        }
        return null;
    }
    public async Task ImportPresentation(ImportJob job, CancellationToken ct)
    {
        string? folder = await FindLibraryFolder(ct);
        if (folder == null) { OpenPresentation(job.PresentationFile); return; }
        string target = Path.Combine(folder, MediaServices.CleanName(job.Name) + "-" + job.Id[..6] + ".pro");
        if (!File.Exists(target)) File.Copy(job.PresentationFile, target, false);
    }
    public async Task Complete(ImportJob job, IProgress<WorkProgress> progress, CancellationToken ct, bool open = true)
    {
        if (!await PresentationReady(job.PresentationId, job.Count, ct) && open) await ImportPresentation(job, ct);
        job.State = "AwaitingPresentation"; job.Save();
        progress.Report(new("Importing into ProPresenter. If prompted, choose a library in ProPresenter…", -1));
        bool ready = false;
        for (int i = 0; i < 90; i++)
        {
            if (await PresentationReady(job.PresentationId, job.Count, ct)) { ready = true; break; }
            await Task.Delay(1000, ct);
        }
        if (!ready) throw new IOException("ProPresenter has not confirmed the new presentation. The prepared files are saved. Complete the library import in ProPresenter, then use Resume import.");
        var presentation = await Get("v1/presentation/" + job.PresentationId.ToLowerInvariant(), ct); presentation = presentation["presentation"] ?? presentation;
        string arrangementId = presentation["current_arrangement"]?.ToString() ?? "";
        job.State = "LinkingPlaylist"; job.Save();
        progress.Report(new("Adding the presentation to your playlist…", -1));
        string route = "v1/playlist/" + job.PlaylistId;
        var playlist = await Get(route, ct); var items = playlist["items"]?.AsArray() ?? throw new IOException("Playlist no longer exists.");
        bool Contains(JsonArray array) => array.Any(n => string.Equals(n?["presentation_info"]?["presentation_uuid"]?.ToString(), job.PresentationId, StringComparison.OrdinalIgnoreCase) || string.Equals(n?["target_uuid"]?.ToString(), job.PresentationId, StringComparison.OrdinalIgnoreCase) || string.Equals(n?["id"]?["uuid"]?.ToString(), job.PresentationId, StringComparison.OrdinalIgnoreCase));
        if (!Contains(items))
        {
            // Save a backup, then detect edits between the two reads. Public API has no CAS/ETag.
            File.WriteAllText(Path.Combine(job.Folder, "playlist-before.json"), items.ToJsonString());
            var latest = (await Get(route, ct))["items"]?.AsArray() ?? throw new IOException("Playlist unavailable.");
            if (!JsonNode.DeepEquals(items, latest)) throw new IOException("The playlist changed while importing. Use Resume import after the operator finishes editing.");
            var updated = WriteItems(items);
            updated.Add(new JsonObject {
                ["id"] = new JsonObject { ["uuid"] = job.PresentationId.ToLowerInvariant(), ["name"] = job.Name, ["index"] = items.Count },
                ["type"] = "presentation", ["is_hidden"] = false, ["is_pco"] = false,
                ["destination"] = "presentation",
                ["target_uuid"] = job.PresentationId,
                ["presentation_info"] = new JsonObject { ["presentation_uuid"] = job.PresentationId, ["arrangement_name"] = "Default", ["arrangement_uuid"] = arrangementId }
            });
            using var response = await http.PutAsJsonAsync(Url(route), updated, ct);
            if (!response.IsSuccessStatusCode) throw new IOException($"ProPresenter rejected the playlist update ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}");
            var verified = (await Get(route, ct))["items"]!.AsArray();
            if (!Contains(verified)) throw new IOException("ProPresenter did not confirm the playlist entry. Resume import to retry.");
            // Existing entries must still be present in the same order.
            if (verified.Count < items.Count + 1 || !items.Zip(verified.Take(items.Count)).All(pair => SamePlaylistItem(pair.First, pair.Second)))
                throw new IOException("Playlist verification found an unexpected edit. Inspect the playlist; the previous contents are saved in playlist-before.json.");
        }
        job.State = "Complete"; job.Error = ""; job.Save();
    }
    public static bool SamePlaylistItem(JsonNode? before, JsonNode? after)
    {
        if (before is not JsonObject original || after is not JsonObject current) return false;
        if (original["id"]?["name"]?.ToString() != current["id"]?["name"]?.ToString()) return false;
        if (original["type"]?.ToString() != "header")
        {
            string? Target(JsonObject item) => item["target_uuid"]?.ToString() ?? item["presentation_info"]?["presentation_uuid"]?.ToString() ?? item["id"]?["uuid"]?.ToString();
            if (!string.Equals(Target(original), Target(current), StringComparison.OrdinalIgnoreCase)) return false;
        }
        // ProPresenter regenerates header UUIDs during PUT. Verify the content and order,
        // including color, duration, flags and arrangements, rather than transient item IDs.
        return original.Where(property => property.Key is not "id" and not "target_uuid").All(property => JsonNode.DeepEquals(property.Value, current[property.Key]));
    }
    public static JsonArray WriteItems(JsonArray items)
    {
        var copy = items.DeepClone().AsArray();
        foreach (var node in copy)
        {
            if (node is not JsonObject item || item["id"] is not JsonObject id) throw new IOException("Playlist contains an invalid item. No update was sent.");
            string? target = item["target_uuid"]?.ToString();
            if (string.IsNullOrWhiteSpace(target)) target = item["presentation_info"]?["presentation_uuid"]?.ToString() ?? id["uuid"]?.ToString();
            if (string.IsNullOrWhiteSpace(target)) throw new IOException("Playlist item has no identifier. No update was sent.");
            // GET omits target_uuid for headers; PUT requires it even for non-target items.
            // A header retains its own UUID, name, color and order.
            item["target_uuid"] = target;
            // GET exposes playlist-item IDs; PUT resolves the referenced object by id.uuid.
            if (item["type"]?.ToString() != "header") id["uuid"] = target;
        }
        return copy;
    }
    public void Dispose() => http.Dispose();
}
