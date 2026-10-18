using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ProPresenterAssistant.Updates;

namespace ProPresenterAssistant;

public sealed class UpdateController(App app, MainWindow owner, CancellationToken lifetime)
{
    private readonly UpdateSchedule schedule = new();
    private UpdateRelease? pending;
    private bool checking, prompting;
    public bool Working { get; private set; }
    public async Task Loop()
    {
        // Wall-clock deadlines catch up immediately after sleep. Short offline retries also cover reconnection.
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                if (schedule.Due(DateTimeOffset.UtcNow)) await Check(false);
                if (pending != null && !owner.Busy && !Working && !prompting) await Offer(pending);
                await Task.Delay(TimeSpan.FromSeconds(30), lifetime);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log(ex); schedule.Failed(DateTimeOffset.UtcNow); }
        }
    }
    public async Task Check(bool manual)
    {
        if (checking || Working || prompting) return;
        checking = true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime); timeout.CancelAfter(TimeSpan.FromSeconds(25));
            using var http = new HttpClient(); var release = await new UpdateClient(http).Check(timeout.Token);
            schedule.Succeeded(DateTimeOffset.UtcNow);
            pending = release != null && (manual || LoadSkipped() != release.Version) ? release : null;
            if (manual && release == null) MessageBox.Show("You’re running the latest version (" + UpdateClient.Current.ToString(3) + ").", "Assistant updates");
        }
        catch (Exception ex) when (!lifetime.IsCancellationRequested)
        {
            schedule.Failed(DateTimeOffset.UtcNow); Log(ex);
            if (manual) MessageBox.Show("Updates could not be checked. " + ex.Message, "Assistant updates");
        }
        finally { checking = false; }
        if (manual && pending != null)
        {
            if (owner.Busy) MessageBox.Show("An update is available. You’ll be asked after the current import finishes.", "Assistant updates");
            else await Offer(pending);
        }
    }
    private static string StateFile => Path.Combine(UpdateSafety.Root, "preferences.json");
    private static string LoadSkipped() { try { return JsonDocument.Parse(File.ReadAllText(StateFile)).RootElement.GetProperty("skip").GetString() ?? ""; } catch { return ""; } }
    private static void Log(Exception ex) { try { Directory.CreateDirectory(UpdateSafety.Root); File.AppendAllText(Path.Combine(UpdateSafety.Root, "errors.log"), DateTimeOffset.Now + " " + ex + Environment.NewLine); } catch { } }
    private async Task Offer(UpdateRelease release)
    {
        prompting = true; pending = null;
        try
        {
            var dialog = new Window { Title = "An assistant update is available", Width = 570, Height = 450, WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize };
            var panel = new StackPanel { Margin = new Thickness(24) }; dialog.Content = panel;
            panel.Children.Add(new TextBlock { Text = "Update to " + release.Version + "?", FontSize = 24 });
            panel.Children.Add(new TextBlock { Text = "Installed: " + UpdateClient.Current.ToString(3) + ". The assistant will close briefly and reopen after installation.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
            panel.Children.Add(new ScrollViewer { Height = 210, Content = new TextBlock { Text = release.Notes, TextWrapping = TextWrapping.Wrap } });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(buttons);
            bool accept = false;
            foreach (string label in new[] { "Skip this version", "Later", "Update now" })
            {
                var button = new Button { Content = label }; buttons.Children.Add(button);
                button.Click += (_, _) => { if (label == "Skip this version") { Directory.CreateDirectory(UpdateSafety.Root); File.WriteAllText(StateFile, JsonSerializer.Serialize(new { skip = release.Version })); } accept = label == "Update now"; dialog.Close(); };
            }
            dialog.ShowDialog();
            if (!accept || lifetime.IsCancellationRequested) return;
            if (owner.Busy) { pending = release; return; }
            string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ProPresenter Assistant", "ProPresenterAssistant.exe");
            if (!string.Equals(Environment.ProcessPath, installed, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Automatic installation is available in the installed app. Download and run the latest installer from GitHub to update this portable copy.", "Assistant updates"); return;
            }
            await Install(release);
        }
        finally { prompting = false; }
    }
    private async Task Install(UpdateRelease release)
    {
        Working = true;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var dialog = new Window { Title = "Updating ProPresenter Assistant", Width = 490, Height = 210, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var panel = new StackPanel { Margin = new Thickness(24) }; dialog.Content = panel;
        var text = new TextBlock { Text = "Downloading update…", TextWrapping = TextWrapping.Wrap }; panel.Children.Add(text);
        var bar = new System.Windows.Controls.ProgressBar { Height = 10, Margin = new Thickness(0, 18, 0, 12) }; panel.Children.Add(bar);
        var stop = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(stop);
        bool committed = false, finished = false;
        stop.Click += (_, _) => cancel.Cancel();
        dialog.Closing += (_, e) => { if (!finished) { e.Cancel = true; if (!committed) cancel.Cancel(); } };
        owner.IsEnabled = false;
        dialog.Loaded += async (_, _) =>
        {
            Process? installer = null; bool exitForUpdate = false;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                var progress = new Progress<WorkProgress>(p => { text.Text = p.Text; bar.IsIndeterminate = p.Fraction < 0; bar.Value = p.Fraction * 100; });
                string file = await new UpdateClient(http).Download(release, progress, cancel.Token);
                text.Text = "Preparing the installer…"; bar.IsIndeterminate = true;
                string token = Guid.NewGuid().ToString("N");
                var start = new ProcessStartInfo(file) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(file)! };
                foreach (string arg in new[] { "--silent-update", "--handoff", token, "--parent", Environment.ProcessId.ToString(), "--parent-start", Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString() }) start.ArgumentList.Add(arg);
                installer = Process.Start(start) ?? throw new IOException("The update installer did not start.");
                using var pipe = new NamedPipeClientStream(".", "PPA-update-" + token, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token); timeout.CancelAfter(TimeSpan.FromMinutes(3));
                while (!pipe.IsConnected)
                {
                    if (installer.HasExited) throw new IOException(File.Exists(Path.Combine(UpdateSafety.Root, "last-result.txt")) ? File.ReadAllText(Path.Combine(UpdateSafety.Root, "last-result.txt")) : "The installer exited before it was ready.");
                    try { await pipe.ConnectAsync(500, timeout.Token); } catch (TimeoutException) { }
                }
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                if (await reader.ReadLineAsync(timeout.Token) != "READY " + release.Version) throw new IOException("The installer version or readiness check failed.");
                await owner.SaveUpdateSession(); cancel.Token.ThrowIfCancellationRequested();
                await writer.WriteLineAsync("COMMIT"); committed = true; stop.IsEnabled = false;
                if (await reader.ReadLineAsync(timeout.Token) != "COMMITTED") throw new IOException("The installer did not acknowledge the update handoff.");
                text.Text = "Installing… The assistant will reopen automatically.";
                // Process.Start creates an independent Windows process. No exit handler kills this installer.
                exitForUpdate = true;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            catch (Exception ex) { Log(ex); MessageBox.Show(dialog, ex.Message, "Update needs attention"); }
            finally { installer?.Dispose(); finished = true; dialog.Close(); owner.IsEnabled = true; Working = false; if (!committed) owner.ResumeUpdatePreviews(); }
            if (exitForUpdate) app.ExitForUpdate();
        };
        dialog.ShowDialog();
        await Task.CompletedTask;
    }
}
