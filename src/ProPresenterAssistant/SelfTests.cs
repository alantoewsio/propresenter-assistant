using System.IO;
using System.IO.Compression;
using System.Text.Json;
namespace ProPresenterAssistant;
public static class SelfTests
{
    public static async Task<int> Run(string report)
    {
        var checks = new List<string>(); string root = Path.Combine(Path.GetTempPath(), "ProPresenterAssistant-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        void Assert(bool condition, string name) { if (!condition) throw new Exception(name); checks.Add(name); }
        try
        {
            var enabledNetwork = ApiDiscovery.ParsePreferences("EnableNetwork=true;\r\nNetworkPort=53532;\r\nEnableLink=true;");
            Assert(enabledNetwork.Enabled == true && enabledNetwork.Port == 53532, "ProPresenter Network preferences report enabled state and selected port");
            var disabledNetwork = ApiDiscovery.ParsePreferences(" NetworkPort = 53532;\n EnableNetwork = false;");
            Assert(disabledNetwork.Enabled == false && disabledNetwork.Port == 53532, "Disabled API distinguished from unreachable API");
            var unknownNetwork = ApiDiscovery.ParsePreferences("EnableNetwork=unexpected; NetworkPort=99999;");
            Assert(unknownNetwork.Enabled == null && unknownNetwork.Port == null, "Unknown or invalid Network preferences do not imply API disabled");
            string requestRoot = Path.Combine(Path.GetTempPath(), "ProPresenterAssistant-Requests"); Directory.CreateDirectory(requestRoot);
            string request = Path.Combine(requestRoot, Guid.NewGuid() + ".txt");
            string firstSource = Path.Combine(root, "First & ünicode.png"), secondSource = Path.Combine(root, "Second video.mp4");
            File.WriteAllText(firstSource, "fixture"); File.WriteAllText(secondSource, "fixture");
            File.WriteAllLines(request, [firstSource, secondSource, firstSource, Path.Combine(root, "missing.png")], System.Text.Encoding.Unicode);
            var explorerPaths = ImportRequests.ReadArguments(["--import-list", request]);
            Assert(explorerPaths.SequenceEqual(new[] { firstSource, secondSource }) && !File.Exists(request), "Explorer multi-selection preserves Unicode paths, deduplicates and cleans request");
            bool rejectedRequest = false;
            try { ImportRequests.ReadArguments(["--import-list", firstSource]); } catch (IOException) { rejectedRequest = true; }
            Assert(rejectedRequest && File.Exists(firstSource), "Explorer request cannot read or delete arbitrary source files");
            File.Delete(firstSource); File.Delete(secondSource);
            Assert(!MediaServices.SafeEntry("../escape.png") && !MediaServices.SafeEntry("C:\\escape.png") && !MediaServices.SafeEntry("/absolute.png") && !MediaServices.SafeEntry("folder\\..\\escape.png"), "Archive traversal and absolute paths rejected");
            Assert(MediaServices.SafeEntry("folder/slide.png"), "Valid archive entry accepted");
            string image = Path.Combine(root, "B.png"); await MediaServices.RunTool("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "color=c=purple:s=320x180", "-frames:v", "1", "-y", image], CancellationToken.None);
            string video = Path.Combine(root, "C.webm"); await MediaServices.RunTool("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "color=c=blue:s=320x180:r=10", "-t", "1", "-c:v", "libvpx-vp9", "-y", video], CancellationToken.None);
            string zip = Path.Combine(root, "Sunday.zip"); using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) { archive.CreateEntryFromFile(image, "folder/A.png"); archive.CreateEntry("../unsafe.png"); archive.CreateEntry("notes.txt"); }
            using var service = new MediaServices(); var progress = new Progress<WorkProgress>();
            var items = service.Scan([root], progress, CancellationToken.None);
            Assert(items.Count == 3 && items[0].Name == "A.png" && items[1].Name == "B.png", "Scan finds media in ZIP and sorts alphabetically"); Assert(service.Warnings.Count == 1, "Unsafe archive entry skipped with warning");
            var thumbnail = await service.Thumbnail(items[0], CancellationToken.None); Assert(thumbnail.Image != null && thumbnail.Image.PixelWidth == 320, "Archived image thumbnail generated");
            string tar = Path.Combine(root, "sample.tar");
            using (var tarStream = File.Create(tar)) using (var writer = new System.Formats.Tar.TarWriter(tarStream)) writer.WriteEntry(image, "slides/B.png");
            var tarItems = service.Scan([tar], progress, CancellationToken.None);
            Assert(tarItems.Count == 1, "TAR media discovery supported");
            using (var input = File.OpenRead(tar)) using (var output = File.Create(tar + ".gz")) using (var gzip = new GZipStream(output, CompressionMode.Compress)) await input.CopyToAsync(gzip);
            var gzipItems = service.Scan([tar + ".gz"], progress, CancellationToken.None);
            Assert(gzipItems.Count == 1, "Compressed TAR media discovery supported");
            string dest = Path.Combine(root, "output"); Directory.CreateDirectory(dest);
            var still = await service.Prepare(items[0], dest, 0, progress, CancellationToken.None); Assert(File.Exists(still.Path) && still.Width == 320, "Selected archive image copied and normalized");
            var movie = await service.Prepare(items[2], dest, 1, progress, CancellationToken.None); var probe = await MediaServices.Probe(movie.Path, CancellationToken.None);
            Assert(Path.GetExtension(movie.Path) == ".mp4" && probe.Codec == "h264", "Unsupported WebM converted to H.264 MP4");
            string alphaSource = Path.Combine(root, "alpha.mov"); await MediaServices.RunTool("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "color=c=red@0.5:s=320x180:r=10", "-t", "0.2", "-vf", "format=argb", "-c:v", "qtrle", "-y", alphaSource], CancellationToken.None);
            var alphaItem = new MediaItem { Source = alphaSource, Name = "alpha.mov", Group = root, IsVideo = true, Size = new FileInfo(alphaSource).Length };
            var alphaMovie = await service.Prepare(alphaItem, dest, 2, progress, CancellationToken.None); var alphaProbe = await MediaServices.Probe(alphaMovie.Path, CancellationToken.None);
            Assert(alphaProbe.Codec == "prores" && alphaProbe.PixelFormat.StartsWith("yuva"), "Unsupported alpha video converted to ProRes 4444 with alpha");
            string id = Guid.NewGuid().ToString(); byte[] presentation = NativePresentation.Create("Test", id, [still, movie]);
            Assert(presentation.Length > 200 && System.Text.Encoding.UTF8.GetString(presentation).Contains(id), "Native presentation contains expected identifier");
            string fixture = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "test-presentation.pro"); File.WriteAllBytes(fixture, presentation);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool stopped = false; try { await service.Stage(items[0], cancelled.Token); } catch (OperationCanceledException) { stopped = true; } Assert(stopped, "Cancelled staging does not read or extract files");
            string browseRoot = Path.Combine(root, "Folder selection"); Directory.CreateDirectory(Path.Combine(browseRoot, "Nested"));
            string clicked = Path.Combine(browseRoot, "Clicked.png"), neighbor = Path.Combine(browseRoot, "Neighbor.png"), nested = Path.Combine(browseRoot, "Nested", "Nested.png");
            foreach (string path in new[] { clicked, neighbor, nested }) File.Copy(image, path);
            File.Copy(video, Path.Combine(browseRoot, "Video.webm")); File.Copy(zip, Path.Combine(browseRoot, "Archive.zip"));
            var pictureSelection = ExplorerSelection.Scan(service, [clicked], true, progress, CancellationToken.None);
            Assert(pictureSelection.Count == 3 && pictureSelection.Single(i => i.Selected).Source == clicked && pictureSelection.Any(i => i.Source == nested), "Explorer picture import shows neighboring and nested pictures with only clicked picture selected");
            var multipleSelection = ExplorerSelection.Scan(service, [clicked, nested], true, progress, CancellationToken.None);
            Assert(multipleSelection.Count == 3 && multipleSelection.Count(i => i.Selected) == 2 && !multipleSelection.Single(i => i.Source == neighbor).Selected, "Explorer multiple-picture selection retains original selection without duplicate thumbnails");
            var folderSelection = ExplorerSelection.Scan(service, [browseRoot], true, progress, CancellationToken.None);
            Assert(folderSelection.Count == 5 && folderSelection.All(i => i.Selected) && folderSelection.Any(i => i.Source == nested), "Explorer folder import recursively selects its images, videos and archived media");
            var directSelection = ExplorerSelection.Scan(service, [clicked], false, progress, CancellationToken.None);
            Assert(directSelection.Count == 1 && directSelection[0].Selected, "Choose-files import preserves explicit-file behavior");
            var mixedSelection = ExplorerSelection.Scan(service, [clicked, Path.Combine(browseRoot, "Video.webm")], true, progress, CancellationToken.None);
            Assert(mixedSelection.Count == 4 && mixedSelection.Count(i => i.Selected) == 2 && mixedSelection.Any(i => i.IsVideo && i.Selected), "Explorer mixed selection keeps explicit video selected and neighboring pictures available");
            string hiddenImage = Path.Combine(browseRoot, "Hidden.png"), hiddenVideo = Path.Combine(browseRoot, "Hidden.webm"), hiddenArchive = Path.Combine(browseRoot, "Hidden.zip");
            File.Copy(image, hiddenImage); File.Copy(video, hiddenVideo); File.Copy(zip, hiddenArchive);
            foreach (string path in new[] { hiddenImage, hiddenVideo, hiddenArchive }) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            string hiddenFolder = Path.Combine(browseRoot, "Hidden folder"); Directory.CreateDirectory(Path.Combine(hiddenFolder, "Nested"));
            string hiddenChild = Path.Combine(hiddenFolder, "Nested", "Visible.png"); File.Copy(image, hiddenChild);
            File.SetAttributes(hiddenFolder, File.GetAttributes(hiddenFolder) | FileAttributes.Hidden);
            var visibleOnly = service.Scan([browseRoot], progress, CancellationToken.None);
            Assert(visibleOnly.Count == 5 && visibleOnly.All(i => !i.Source.Contains("Hidden", StringComparison.Ordinal)), "Folder scan skips hidden images, videos, archives and entire hidden subtrees");
            Assert(service.Scan([hiddenImage, hiddenVideo, hiddenArchive, hiddenFolder], progress, CancellationToken.None).Count == 0, "Explicit imports skip hidden media, archives and folders");
            var visibleExplorer = ExplorerSelection.Scan(service, [clicked], true, progress, CancellationToken.None);
            Assert(visibleExplorer.Count == 3 && visibleExplorer.Single(i => i.Selected).Source == clicked, "Explorer neighboring-picture scan excludes hidden media while preserving clicked selection");
            await ApiTests.Run(Assert, root);
            File.WriteAllText(report, JsonSerializer.Serialize(new { passed = true, checks }, new JsonSerializerOptions { WriteIndented = true })); return 0;
        }
        catch (Exception ex) { File.WriteAllText(report, JsonSerializer.Serialize(new { passed = false, checks, error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true })); return 1; }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
