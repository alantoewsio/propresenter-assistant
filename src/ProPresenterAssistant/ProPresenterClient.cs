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
        using var response = await http.GetAsync(Url(route), ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"ProPresenter API GET /{route} returned {(int)response.StatusCode} ({response.ReasonPhrase}): {await response.Content.ReadAsStringAsync(ct)}", null, response.StatusCode);
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
        try { return await DiscoverLibraryFolder(ct); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Library discovery is optional. Older APIs or stale library references
            // must not prevent the native file-open import path from being used.
            return null;
        }
    }
    private async Task<string?> DiscoverLibraryFolder(CancellationToken ct)
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
            LegacyMediaMap? legacy = null;
            var preserved = items;
            if (items.OfType<JsonObject>().Any(item => item["type"]?.ToString() == "media" && string.IsNullOrWhiteSpace(item["target_uuid"]?.ToString())))
            {
                progress.Report(new("Resolving existing media references from ProPresenter’s playlist files…", -1));
                legacy = await LoadLegacyMediaMap(job.PlaylistId, presentation, ct);
                preserved = legacy.Normalize(items, false);
                File.WriteAllText(Path.Combine(job.Folder, "playlist-resolved.json"), preserved.ToJsonString());
                var unchanged = (await Get(route, ct))["items"]?.AsArray();
                if (!JsonNode.DeepEquals(items, unchanged)) throw new IOException("The playlist changed while resolving media references. No update was sent. Use Resume import.");
            }
            var updated = WriteItems(preserved);
            updated.Add(new JsonObject {
                ["id"] = new JsonObject { ["uuid"] = job.PresentationId.ToLowerInvariant(), ["name"] = job.Name, ["index"] = items.Count },
                ["type"] = "presentation", ["is_hidden"] = false, ["is_pco"] = false,
                ["destination"] = "presentation",
                ["target_uuid"] = job.PresentationId,
                ["presentation_info"] = new JsonObject { ["presentation_uuid"] = job.PresentationId, ["arrangement_name"] = "Default", ["arrangement_uuid"] = arrangementId }
            });
            legacy?.EnsureUnchanged();
            using var response = await PutPlaylist(job.PlaylistId, route, items, updated, progress, ct);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct);
                string attempted = response.RequestMessage?.RequestUri?.AbsolutePath ?? "/" + route;
                progress.Report(new("Saving import diagnostics…", -1));
                string diagnostics = await SaveDiagnostics(job, route, attempted, updated, items, presentation, (int)response.StatusCode, detail, ct);
                string recovery = response.StatusCode == HttpStatusCode.NotFound
                    ? $" The presentation was confirmed in the library, but playlist linking failed. In ProPresenter, drag ‘{job.Name}’ from the library into the intended playlist, then choose Resume import in the assistant. Prepared files are saved in {job.Folder}."
                    : "";
                throw new IOException($"ProPresenter API PUT /{route} rejected the playlist update ({(int)response.StatusCode}): {detail}." + (attempted != "/" + route ? $" Compatibility request PUT {attempted} also failed." : "") + recovery + diagnostics);
            }
            var verified = (await Get(route, ct))["items"]!.AsArray();
            if (!Contains(verified)) throw new IOException("ProPresenter did not confirm the playlist entry. Resume import to retry.");
            if (legacy != null) verified = legacy.Normalize(verified, true);
            // Existing entries must still be present in the same order.
            if (verified.Count != items.Count + 1 || !preserved.Zip(verified.Take(items.Count)).All(pair => SamePlaylistItem(pair.First, pair.Second)))
                throw new IOException("Playlist verification found an unexpected edit. Inspect the playlist; the previous contents are saved in playlist-before.json.");
        }
        job.State = "Complete"; job.Error = ""; job.Save();
    }
    private async Task<LegacyMediaMap> LoadLegacyMediaMap(string playlistId, JsonNode presentation, CancellationToken ct)
    {
        var roots = new List<string>();
        string? path = presentation["presentation_path"]?.ToString();
        if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
        {
            string? parent = Path.GetDirectoryName(path);
            for (int i = 0; parent != null && i < 5; i++, parent = Path.GetDirectoryName(parent)) roots.Add(parent);
        }
        if (!string.IsNullOrWhiteSpace(settings.LibraryFolder)) roots.Add(Path.GetDirectoryName(Path.GetDirectoryName(settings.LibraryFolder)!)!);
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ProPresenter"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming", "RenewedVision", "ProPresenter", "LocalWorkspaces", "ProPresenter"));
        string? directory = roots.Distinct(StringComparer.OrdinalIgnoreCase).Select(r => Path.Combine(r, "Playlists"))
            .FirstOrDefault(r => File.Exists(Path.Combine(r, "Library")) && File.Exists(Path.Combine(r, "Media")));
        if (directory == null) throw new IOException("Cannot find ProPresenter’s native playlist files needed to preserve existing media on this API version. No playlist update was sent.");
        var map = LegacyMediaMap.Read(Path.Combine(directory, "Library"), Path.Combine(directory, "Media"), playlistId);
        var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tree = (await Get("v1/media/playlists", ct)).AsArray();
        async Task Walk(JsonArray nodes)
        {
            foreach (var node in nodes)
            {
                ct.ThrowIfCancellationRequested();
                if (node?["children"] is JsonArray children && children.Count > 0) await Walk(children);
                string? uuid = node?["id"]?["uuid"]?.ToString();
                if (string.IsNullOrWhiteSpace(uuid) || node?["type"]?.ToString() is "group" or "folder") continue;
                var content = await Get("v1/media/playlist/" + uuid, ct);
                foreach (var item in content["items"]?.AsArray() ?? []) if (item?["id"]?["uuid"] is JsonNode id) valid.Add(id.ToString());
            }
        }
        await Walk(tree);
        if (map.Targets.Any(target => !valid.Contains(target.CueId))) throw new IOException("The stored media references no longer match ProPresenter’s live media bin. No playlist update was sent.");
        return map;
    }
    private async Task<string> SaveDiagnostics(ImportJob job, string originalRoute, string attemptedRoute, JsonArray request, JsonArray before, JsonNode presentation, int status, string body, CancellationToken ct)
    {
        var report = new JsonObject
        {
            ["recorded_at_utc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["assistant_version"] = typeof(ProPresenterClient).Assembly.GetName().Version?.ToString(),
            ["import_name"] = job.Name, ["playlist_uuid"] = job.PlaylistId, ["presentation_uuid"] = job.PresentationId,
            ["original_request"] = "PUT /" + originalRoute, ["last_request"] = "PUT " + attemptedRoute,
            ["status"] = status, ["response_body"] = body,
            ["submitted_items"] = request.DeepClone(), ["playlist_before"] = before.DeepClone(),
            ["confirmed_presentation"] = presentation.DeepClone()
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var responses = new JsonObject(); report["read_only_responses"] = responses;
        foreach (string route in new[] { "version", "v1/playlists", originalRoute })
        {
            try { responses["GET /" + route] = (await Get(route, deadline.Token)).DeepClone(); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException)
            {
                responses["GET /" + route] = new JsonObject { ["error"] = ex.Message };
            }
        }
        try
        {
            string path = Path.Combine(job.Folder, "import-diagnostics.json");
            File.WriteAllText(path, report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return $" Diagnostics saved to {path}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $" Could not save diagnostics: {ex.Message}"; }
    }
    private async Task<HttpResponseMessage> PutPlaylist(string playlistId, string route, JsonArray before, JsonArray updated, IProgress<WorkProgress> progress, CancellationToken ct)
    {
        var response = await http.PutAsJsonAsync(Url(route), updated, ct);
        if (response.StatusCode != HttpStatusCode.NotFound) return response;
        try
        {
            // Retry a UUID-path 404 through the documented index path.
            // Resolve its complete folder hierarchy rather than assuming a flat index.
            var tree = (await Get("v1/playlists", ct)).AsArray();
            var paths = new List<string>();
            void Walk(JsonArray nodes, string parent)
            {
                for (int index = 0; index < nodes.Count; index++)
                {
                    var node = nodes[index];
                    string path = parent + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (string.Equals(node?["id"]?["uuid"]?.ToString(), playlistId, StringComparison.OrdinalIgnoreCase)) paths.Add(path);
                    if (node?["children"] is JsonArray children) Walk(children, path + "/");
                }
            }
            Walk(tree, "");
            if (paths.Count != 1) return response;
            string alternate = "v1/playlist/" + paths[0];
            var resolved = await Get(alternate, ct);
            if (!string.Equals(resolved["id"]?["uuid"]?.ToString(), playlistId, StringComparison.OrdinalIgnoreCase) || !JsonNode.DeepEquals(before, resolved["items"]))
                throw new IOException("The playlist changed or its alternative API path resolves to a different playlist. No compatibility update was sent. Resume import after checking the playlist.");
            var latest = await Get(route, ct);
            if (!JsonNode.DeepEquals(before, latest["items"]))
                throw new IOException("The playlist changed after the first update attempt. No compatibility update was sent. Use Resume import to verify the saved presentation.");
            progress.Report(new("Retrying playlist linking using its verified folder/index path…", -1));
            response.Dispose();
            return await http.PutAsJsonAsync(Url(alternate), updated, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return response; }
        catch { response.Dispose(); throw; }
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
