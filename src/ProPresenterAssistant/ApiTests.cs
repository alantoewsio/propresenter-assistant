using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json.Nodes;
namespace ProPresenterAssistant;
public static class ApiTests
{
    public static async Task Run(Action<bool, string> assert, string root)
    {
        var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); int port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var items = JsonNode.Parse("[{\"id\":{\"uuid\":\"existing\",\"name\":\"Existing\",\"index\":0},\"type\":\"header\",\"is_hidden\":false,\"is_pco\":false,\"destination\":\"presentation\",\"header_color\":{\"red\":0.3,\"green\":0.4,\"blue\":0.6,\"alpha\":1.0}},{\"id\":{\"uuid\":\"existing-item\",\"name\":\"Existing presentation\",\"index\":1},\"type\":\"presentation\",\"target_uuid\":\"existing-presentation\",\"duration\":20,\"presentation_info\":{\"presentation_uuid\":\"existing-presentation\",\"arrangement_uuid\":\"existing-arrangement\"}}]")!.AsArray();
        int writes = 0, reads = 0; bool conflict = false, unrelatedServer = false, rejectUpdate = false, missingLibraries = false, uuidOnly404 = false, wrongAlternate = false;
        int alternateWrites = 0;
        bool legacyMode = false;
        var legacyFixture = LegacyMediaTests.Create(root);
        JsonArray? lastSubmitted = null;
        using var stop = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext context; try { context = await listener.GetContextAsync(); } catch { break; }
                string route = context.Request.Url!.AbsolutePath; string json = "{}";
                if (route == "/version") json = unrelatedServer ? "{\"host_description\":\"Unrelated server\",\"api_version\":\"v1\"}" : "{\"host_description\":\"ProPresenter 21.4.2\",\"api_version\":\"v1\"}";
                else if (route.StartsWith("/v1/presentation/")) json = new JsonObject { ["presentation"] = new JsonObject { ["groups"] = new JsonArray(new JsonObject { ["slides"] = new JsonArray(new JsonObject(), new JsonObject()) }), ["current_arrangement"] = "arrangement", ["presentation_path"] = Path.Combine(legacyFixture.Folder, "Libraries", "Default", "Imported.pro") } }.ToJsonString();
                else if (route == "/v1/media/playlists") json = "[{\"id\":{\"uuid\":\"bin\"},\"type\":\"playlist\"}]";
                else if (route == "/v1/media/playlist/bin") json = "{\"items\":[{\"id\":{\"uuid\":\"bin-cue-a\"}},{\"id\":{\"uuid\":\"bin-cue-b\"}}]}";
                else if (route == "/v1/playlists") json = "[{\"id\":{\"uuid\":\"folder\",\"name\":\"Services\"},\"type\":\"folder\",\"children\":[{\"id\":{\"uuid\":\"playlist\",\"name\":\"Sunday\"},\"type\":\"playlist\"}]}]";
                else if (route == "/v1/libraries") { if (missingLibraries) context.Response.StatusCode = 404; else context.Response.StatusCode = 500; }
                else if (route == "/v1/playlist/playlist" || route == "/v1/playlist/0/0")
                {
                    if (context.Request.HttpMethod == "PUT")
                    {
                        using var reader = new StreamReader(context.Request.InputStream); var submitted = JsonNode.Parse(await reader.ReadToEndAsync())!.AsArray();
                        if (route == "/v1/playlist/0/0") alternateWrites++;
                        if (rejectUpdate || (uuidOnly404 && route == "/v1/playlist/playlist")) { context.Response.StatusCode = 404; json = "Not Found"; }
                        else if (submitted.Any(item => string.IsNullOrWhiteSpace(item?["target_uuid"]?.ToString()))) { context.Response.StatusCode = 400; json = "Json deserialize error: missing field `target_uuid`"; }
                        else
                        {
                            lastSubmitted = submitted.DeepClone().AsArray(); items = submitted;
                            if (legacyMode) foreach (var item in items.OfType<JsonObject>().Where(item => item["type"]?.ToString() == "media"))
                            {
                                if (item["id"]!["uuid"]!.ToString() is not ("bin-cue-a" or "bin-cue-b")) throw new Exception("Legacy PUT attempted to use an unresolvable media-object UUID");
                                item["id"]!["uuid"] = item["id"]!["uuid"]!.ToString().Replace("cue", "object"); item.Remove("target_uuid");
                            }
                            foreach (var item in items.OfType<JsonObject>().Where(item => item["type"]?.ToString() == "header")) { item["id"]!["uuid"] = Guid.NewGuid().ToString(); item.Remove("target_uuid"); }
                            writes++; context.Response.StatusCode = 204;
                        }
                    }
                    else { reads++; var value = items.DeepClone().AsArray(); if (conflict && reads % 2 == 0) value.Add(new JsonObject { ["id"] = new JsonObject { ["uuid"] = "operator-edit" } }); json = new JsonObject { ["id"] = new JsonObject { ["uuid"] = wrongAlternate && route == "/v1/playlist/0/0" ? "different-playlist" : "playlist" }, ["items"] = value }.ToJsonString(); }
                }
                byte[] data = System.Text.Encoding.UTF8.GetBytes(json); context.Response.ContentType = "application/json"; if (context.Response.StatusCode != 204) await context.Response.OutputStream.WriteAsync(data); context.Response.Close();
            }
        });
        try
        {
            assert(await ApiDiscovery.IsApi(port, CancellationToken.None), "API discovery verifies ProPresenter version response");
            unrelatedServer = true;
            assert(!await ApiDiscovery.IsApi(port, CancellationToken.None), "API discovery rejects unrelated HTTP servers");
            unrelatedServer = false;
            using var client = new ProPresenterClient(new Settings { Port = port });
            var playlists = await client.Playlists(CancellationToken.None); assert(playlists.Count == 1 && playlists[0].Name == "Services / Sunday", "Playlist hierarchy flattened correctly");
            var job = new ImportJob { Name = "Imported", PlaylistId = "playlist", Folder = root, Count = 2 };
            await client.Complete(job, new Progress<WorkProgress>(), CancellationToken.None, false);
            assert(writes == 1 && items.Count == 3 && items[0]!["id"]!["name"]!.ToString() == "Existing", "Playlist append preserves existing entries");
            assert(lastSubmitted![0]!["target_uuid"]!.ToString() == "existing" && lastSubmitted[0]!["id"]!["uuid"]!.ToString() == "existing" && items[0]!["header_color"]!["green"]!.GetValue<double>() == 0.4, "Header PUT supplies target UUID and verifies content after server regenerates its item ID");
            assert(items[1]!["target_uuid"]!.ToString() == "existing-presentation" && items[1]!["duration"]!.GetValue<int>() == 20 && items[1]!["presentation_info"]!["arrangement_uuid"]!.ToString() == "existing-arrangement", "Existing presentation target, duration and arrangement survive normalization");
            assert(items[2]!["presentation_info"]!["arrangement_uuid"]!.ToString() == "arrangement" && items[2]!["id"]!["uuid"]!.ToString() == job.PresentationId, "Playlist write uses live API target and arrangement identifiers");
            var changedHeader = items[0]!.DeepClone(); changedHeader["header_color"]!["green"] = 0.9;
            assert(!ProPresenterClient.SamePlaylistItem(items[0], changedHeader), "Playlist verification detects header color changes despite regenerated IDs");
            await client.Complete(job, new Progress<WorkProgress>(), CancellationToken.None, false);
            assert(writes == 1 && items.Count == 3, "Resume does not duplicate playlist entries");
            conflict = true; reads = 0;
            var other = new ImportJob { Name = "Conflict", PlaylistId = "playlist", Folder = root, Count = 2 };
            bool caught = false; try { await client.Complete(other, new Progress<WorkProgress>(), CancellationToken.None, false); } catch (IOException) { caught = true; }
            assert(caught && writes == 1, "Concurrent playlist edit detected before write");
            conflict = false; rejectUpdate = true;
            var unsupported = new ImportJob { Name = "Older API import", PlaylistId = "playlist", Folder = root, Count = 2 };
            string error = "";
            try { await client.Complete(unsupported, new Progress<WorkProgress>(), CancellationToken.None, false); } catch (IOException ex) { error = ex.Message; }
            assert(error.Contains("PUT /v1/playlist/playlist") && error.Contains("Resume import") && unsupported.State == "LinkingPlaylist" && writes == 1 && items.Count == 3, "Playlist 404 identifies endpoint, preserves saved import and supplies manual recovery without changing existing entries");
            var diagnostic = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "import-diagnostics.json")))!;
            assert(diagnostic["status"]!.GetValue<int>() == 404 && diagnostic["last_request"]!.ToString() == "PUT /v1/playlist/0/0" && diagnostic["submitted_items"]!.AsArray().Count == 4 && diagnostic["confirmed_presentation"]!["groups"]!.AsArray().Count == 1 && diagnostic["read_only_responses"]!["GET /version"]!["host_description"]!.ToString().Contains("ProPresenter"), "Failed playlist update saves exact payload, attempted path, version and confirmed presentation for diagnosis using read-only requests");
            missingLibraries = true;
            assert(await client.FindLibraryFolder(CancellationToken.None) == null, "Library discovery 404 falls back to native file-open import");
            missingLibraries = false; bool failedServer = false;
            try { await client.FindLibraryFolder(CancellationToken.None); } catch (HttpRequestException ex) { failedServer = ex.StatusCode == HttpStatusCode.InternalServerError && ex.Message.Contains("GET /v1/libraries"); }
            assert(failedServer, "Library server errors retain status and endpoint instead of silently falling back");
            rejectUpdate = false; uuidOnly404 = true;
            var legacy = new ImportJob { Name = "Legacy UUID lookup", PlaylistId = "playlist", Folder = root, Count = 2 };
            int previousAlternateWrites = alternateWrites;
            await client.Complete(legacy, new Progress<WorkProgress>(), CancellationToken.None, false);
            assert(legacy.State == "Complete" && writes == 2 && alternateWrites == previousAlternateWrites + 1 && items.Count == 4, "UUID PUT 404 retries through verified nested index path and verifies the original playlist");
            await client.Complete(legacy, new Progress<WorkProgress>(), CancellationToken.None, false);
            assert(writes == 2 && items.Count == 4, "Compatibility import resume does not duplicate entries");
            wrongAlternate = true; previousAlternateWrites = alternateWrites;
            var mismatch = new ImportJob { Name = "Moved playlist", PlaylistId = "playlist", Folder = root, Count = 2 };
            error = ""; try { await client.Complete(mismatch, new Progress<WorkProgress>(), CancellationToken.None, false); } catch (IOException ex) { error = ex.Message; }
            assert(error.Contains("different playlist") && writes == 2 && alternateWrites == previousAlternateWrites, "Compatibility fallback refuses a path that resolves to another playlist");
            wrongAlternate = false; uuidOnly404 = false; legacyMode = true; items = legacyFixture.Items.DeepClone().AsArray();
            var legacyJob = new ImportJob { Name = "Legacy media append", PlaylistId = "playlist", Folder = root, Count = 2 };
            await client.Complete(legacyJob, new Progress<WorkProgress>(), CancellationToken.None, false);
            assert(legacyJob.State == "Complete" && items.Count == 3 && lastSubmitted![0]!["id"]!["uuid"]!.ToString() == "bin-cue-a" && lastSubmitted[1]!["id"]!["uuid"]!.ToString() == "bin-cue-b" && items[1]!["is_hidden"]!.GetValue<bool>() && items[0]!["duration"]!.GetValue<int>() == 5, "Complete legacy import automatically resolves and validates media-bin IDs, appends once and verifies regenerated objects without losing flags or durations");
        }
        finally { stop.Cancel(); listener.Stop(); await server; }
    }
}
