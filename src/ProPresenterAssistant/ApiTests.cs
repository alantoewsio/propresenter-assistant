using System.IO;
using System.Net;
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
        int writes = 0, reads = 0; bool conflict = false, unrelatedServer = false;
        JsonArray? lastSubmitted = null;
        using var stop = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext context; try { context = await listener.GetContextAsync(); } catch { break; }
                string route = context.Request.Url!.AbsolutePath; string json = "{}";
                if (route == "/version") json = unrelatedServer ? "{\"host_description\":\"Unrelated server\",\"api_version\":\"v1\"}" : "{\"host_description\":\"ProPresenter 21.4.2\",\"api_version\":\"v1\"}";
                else if (route.StartsWith("/v1/presentation/")) json = "{\"presentation\":{\"groups\":[{\"slides\":[{},{}]}],\"current_arrangement\":\"arrangement\"}}";
                else if (route == "/v1/playlists") json = "[{\"id\":{\"uuid\":\"folder\",\"name\":\"Services\"},\"type\":\"folder\",\"children\":[{\"id\":{\"uuid\":\"playlist\",\"name\":\"Sunday\"},\"type\":\"playlist\"}]}]";
                else if (route == "/v1/playlist/playlist")
                {
                    if (context.Request.HttpMethod == "PUT")
                    {
                        using var reader = new StreamReader(context.Request.InputStream); var submitted = JsonNode.Parse(await reader.ReadToEndAsync())!.AsArray();
                        if (submitted.Any(item => string.IsNullOrWhiteSpace(item?["target_uuid"]?.ToString()))) { context.Response.StatusCode = 400; json = "Json deserialize error: missing field `target_uuid`"; }
                        else
                        {
                            lastSubmitted = submitted.DeepClone().AsArray(); items = submitted;
                            foreach (var item in items.OfType<JsonObject>().Where(item => item["type"]?.ToString() == "header")) { item["id"]!["uuid"] = Guid.NewGuid().ToString(); item.Remove("target_uuid"); }
                            writes++; context.Response.StatusCode = 204;
                        }
                    }
                    else { reads++; var value = items.DeepClone().AsArray(); if (conflict && reads % 2 == 0) value.Add(new JsonObject { ["id"] = new JsonObject { ["uuid"] = "operator-edit" } }); json = new JsonObject { ["items"] = value }.ToJsonString(); }
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
        }
        finally { stop.Cancel(); listener.Stop(); await server; }
    }
}
