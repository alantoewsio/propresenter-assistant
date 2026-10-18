using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;
using ProPresenterAssistant.Updates;
namespace ProPresenterAssistant;
public partial class App : Application
{
    private Mutex? mutex;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private readonly CancellationTokenSource lifetime = new();
    private MainWindow? window;
    private UpdateController? updates;
    public void ExitForUpdate() { window!.ExitApp(); Shutdown(); }
    private static string Channel => "ProPresenterAssistant-" + WindowsIdentity.GetCurrent().User!.Value.Replace('-', '_');
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 3 && e.Args[0] == "--resume-job")
        {
            try
            {
                var job = JsonSerializer.Deserialize<ImportJob>(File.ReadAllText(e.Args[1])) ?? throw new IOException("Invalid saved import.");
                if (string.IsNullOrWhiteSpace(job.PresentationFile) || !File.Exists(job.PresentationFile)) throw new IOException("Prepared presentation file is unavailable.");
                var settings = Settings.Load(); var status = await ApiDiscovery.Detect(settings, CancellationToken.None);
                if (status.ActivePort == null) throw new IOException(status.Message); settings.Port = status.ActivePort.Value;
                using var client = new ProPresenterClient(settings);
                if (!await client.PresentationReady(job.PresentationId, job.Count, CancellationToken.None)) throw new IOException("Saved presentation is not indexed; resume it in the assistant.");
                await client.Complete(job, new Progress<WorkProgress>(), CancellationToken.None, false);
                File.WriteAllText(e.Args[2], JsonSerializer.Serialize(new { passed = true, job }, new JsonSerializerOptions { WriteIndented = true })); Shutdown();
            }
            catch (Exception ex) { File.WriteAllText(e.Args[2], JsonSerializer.Serialize(new { passed = false, error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true })); Shutdown(1); }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--api-discovery-test")
        {
            var status = await ApiDiscovery.Detect(new Settings { Port = 1, AutoDetectApi = true }, CancellationToken.None);
            File.WriteAllText(e.Args[1], JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true })); Shutdown(status.ActivePort == null ? 1 : 0); return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--playlist-ui-test")
        {
            var testWindow = new MainWindow();
            try
            {
                testWindow.Show(); await testWindow.RefreshAvailablePlaylists();
                if (testWindow.Settings.AutoDetectApi && testWindow.Settings.Port != ApiDiscovery.ReadPreferences().Port) throw new IOException("Automatic port selection did not follow ProPresenter preferences.");
                if (testWindow.DisplayedPlaylists.Count == 0) throw new IOException("Startup did not populate the playlist dropdown.");
                if (!testWindow.PlaylistBox.IsEnabled) throw new IOException("Connected playlist dropdown is disabled.");
                testWindow.PlaylistBox.SelectedIndex = testWindow.DisplayedPlaylists.Count - 1;
                string selected = ((Playlist)testWindow.PlaylistBox.SelectedItem).Id;
                testWindow.PlaylistBox.IsDropDownOpen = true;
                await testWindow.RefreshAvailablePlaylists();
                if (((Playlist)testWindow.PlaylistBox.SelectedItem).Id != selected) throw new IOException("Refresh changed the selected playlist.");
                testWindow.PlaylistBox.IsDropDownOpen = false;
                int port = testWindow.Settings.Port;
                bool autoDetect = testWindow.Settings.AutoDetectApi; testWindow.Settings.AutoDetectApi = false;
                testWindow.Settings.Port = 1;
                await testWindow.RefreshAvailablePlaylists();
                if (testWindow.PlaylistBox.IsEnabled) throw new IOException("Unavailable API did not disable the dropdown.");
                testWindow.Settings.Port = port;
                testWindow.Settings.AutoDetectApi = autoDetect;
                await testWindow.RefreshAvailablePlaylists();
                if (!testWindow.PlaylistBox.IsEnabled) throw new IOException("Reconnection did not enable the dropdown.");
                testWindow.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)testWindow.ActualWidth, (int)testWindow.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(testWindow);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(e.Args[2])) encoder.Save(file);
                File.WriteAllText(e.Args[1], JsonSerializer.Serialize(new { passed = true, count = testWindow.DisplayedPlaylists.Count, playlists = testWindow.DisplayedPlaylists, selectionPreserved = true, expandRefresh = true, disabledWhenUnavailable = true, reconnected = true }, new JsonSerializerOptions { WriteIndented = true }));
                testWindow.ExitApp(); Shutdown();
            }
            catch (Exception ex) { File.WriteAllText(e.Args[1], JsonSerializer.Serialize(new { passed = false, error = ex.ToString() })); testWindow.ExitApp(); Shutdown(1); }
            return;
        }
        if (e.Args.Length == 4 && e.Args[0] == "--live-probe") { int result = await LiveProbe.Run(int.Parse(e.Args[1]), e.Args[2], e.Args[3]); Shutdown(result); return; }
        DispatcherUnhandledException += (_, a) => { Directory.CreateDirectory(Settings.DataRoot); File.AppendAllText(Path.Combine(Settings.DataRoot, "errors.log"), DateTime.Now + " " + a.Exception + "\n"); MessageBox.Show(a.Exception.Message, "ProPresenter Assistant"); a.Handled = true; };
        if (e.Args.Contains("--self-test")) { int code = await SelfTests.Run(e.Args.LastOrDefault() ?? "self-test.json"); Shutdown(code); return; }
        string? restartToken = e.Args.Length == 2 && e.Args[0] == "--update-started" && Guid.TryParseExact(e.Args[1], "N", out _) ? e.Args[1] : null;
        NamedPipeClientStream? restart = null;
        StreamReader? restartReader = null;
        if (restartToken != null)
        {
            try
            {
                restart = new NamedPipeClientStream(".", "PPA-restart-" + restartToken, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await restart.ConnectAsync(timeout.Token);
                restartReader = new StreamReader(restart, System.Text.Encoding.UTF8, false, 1024, true);
                if (await restartReader.ReadLineAsync(timeout.Token) != "START " + UpdateClient.Current.ToString(3)) throw new IOException("Update startup version mismatch.");
            }
            catch { restart?.Dispose(); Shutdown(1); return; }
        }
        else if (UpdateSafety.Installing()) { MessageBox.Show("An update is being installed. The assistant will reopen automatically.", "Assistant update"); Shutdown(); return; }
        bool demo = e.Args.Contains("--demo"); mutex = new Mutex(true, "Local\\" + Channel + (demo ? "-demo" : ""), out bool first);
        string[] importPaths;
        try { importPaths = ImportRequests.ReadArguments(e.Args); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Explorer import needs attention"); Shutdown(1); return; }
        if (!first)
        {
            try { using var pipe = new NamedPipeClientStream(".", Channel, PipeDirection.Out, PipeOptions.Asynchronous); await pipe.ConnectAsync(5000); using var writer = new StreamWriter(pipe); await writer.WriteLineAsync(JsonSerializer.Serialize(new IncomingImport(importPaths, e.Args.Contains("--import-list")))); }
            catch { MessageBox.Show("The assistant is already running, but could not receive these files. Open it from the system tray and choose the files."); }
            Shutdown(); return;
        }
        window = new MainWindow(); MainWindow = window;
        if (demo)
        {
            window.Demo(); window.Show(); int at = Array.IndexOf(e.Args, "--screenshot");
            if (at >= 0 && at + 1 < e.Args.Length) { await Task.Delay(500); window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(e.Args[at + 1]); encoder.Save(file); Shutdown(); }
            return;
        }
        using (var iconStream = typeof(App).Assembly.GetManifestResourceStream("app-icon.ico")!)
        using (var icon = new System.Drawing.Icon(iconStream, 32, 32)) trayIcon = (System.Drawing.Icon)icon.Clone();
        tray = new Forms.NotifyIcon { Text = "ProPresenter Assistant", Icon = trayIcon, Visible = true };
        updates = new UpdateController(this, window, lifetime.Token);
        var menu = new Forms.ContextMenuStrip(); menu.Items.Add("Open assistant", null, (_, _) => window.ShowApp()); menu.Items.Add("Check for updates…", null, async (_, _) => await updates.Check(true)); menu.Items.Add("Exit", null, (_, _) => { if (updates.Working) { MessageBox.Show("Cancel the download in the update window, or wait for installation to finish.", "Assistant update"); return; } if (window.Busy && MessageBox.Show("An import is running. Cancel and exit?", "ProPresenter Assistant", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; window.ExitApp(); Shutdown(); }); tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => window.ShowApp();
        if (restart != null)
        {
            try
            {
                window.IsEnabled = false; window.ShowApp(); await window.RestoreUpdateSession();
                using var writer = new StreamWriter(restart, new System.Text.UTF8Encoding(false), 1024, true) { AutoFlush = true };
                await writer.WriteLineAsync("HEALTHY " + UpdateClient.Current.ToString(3));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                if (await restartReader!.ReadLineAsync(timeout.Token) != "SUCCESS") throw new IOException("Installer did not confirm update completion.");
                try { window.FinishUpdateSession(); } catch { }
                window.IsEnabled = true;
                window.StatusText.Text = "Updated to " + UpdateClient.Current.ToString(3) + ".";
            }
            catch { window.ExitApp(); Shutdown(1); return; }
            finally { restartReader?.Dispose(); restart.Dispose(); }
        }
        else if (e.Args.Contains("--update-recovered"))
        {
            window.ShowApp();
            try { await window.RestoreUpdateSession(); window.FinishUpdateSession(); MessageBox.Show(File.ReadAllText(Path.Combine(UpdateSafety.Root, "last-result.txt")), "Assistant update needs attention"); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Assistant update recovery"); }
        }
        _ = updates.Loop();
        _ = PipeLoop(); _ = UsbLoop(); if (importPaths.Length > 0) await window.Receive(importPaths, false, e.Args.Contains("--import-list")); else if (!e.Args.Contains("--tray")) window.Show();
    }
    private static bool FileOrFolder(string arg) => !arg.StartsWith("--") && (File.Exists(arg) || Directory.Exists(arg));
    private async Task PipeLoop()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try { using var server = new NamedPipeServerStream(Channel, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await server.WaitForConnectionAsync(lifetime.Token); using var reader = new StreamReader(server); string? line = await reader.ReadLineAsync(lifetime.Token); if (line == null || line.Length > 16 * 1024 * 1024) continue; var request = line.TrimStart().StartsWith('[') ? new IncomingImport(JsonSerializer.Deserialize<string[]>(line) ?? []) : JsonSerializer.Deserialize<IncomingImport>(line); if (request == null) continue; await Dispatcher.InvokeAsync(() => { if (request.Paths.Length == 0) window!.ShowApp(); else _ = window!.Receive(request.Paths.Where(FileOrFolder).ToArray(), false, request.Explorer); }); }
            catch (OperationCanceledException) { break; } catch { await Task.Delay(1000); }
        }
    }
    private async Task UsbLoop()
    {
        var known = await Task.Run(WindowsIntegration.UsbVolumes);
        while (!lifetime.IsCancellationRequested)
        {
            try { await Task.Delay(2500, lifetime.Token); if (updates?.Working == true) continue; var current = await Task.Run(WindowsIntegration.UsbVolumes, lifetime.Token); var added = current.Except(known, StringComparer.OrdinalIgnoreCase).ToArray(); known = current; if (added.Length > 0) await Dispatcher.InvokeAsync(() => { if (window!.Settings.MonitorUsb) _ = window.Receive(added, true); }); }
            catch (OperationCanceledException) { break; } catch { }
        }
    }
    protected override void OnExit(ExitEventArgs e) { lifetime.Cancel(); tray?.Dispose(); trayIcon?.Dispose(); mutex?.Dispose(); base.OnExit(e); }
}
