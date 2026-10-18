using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProPresenterAssistant;

public partial class MainWindow : Window
{
    public Settings Settings { get; } = Settings.Load();
    private MediaServices media = new();
    private List<MediaItem> items = [];
    private CancellationTokenSource? operation, previews;
    private Task previewTask = Task.CompletedTask;
    private string screen = "welcome";
    private int page;
    private string? lastFolder;
    private bool exiting;
    private readonly Queue<(string[] Paths, bool Usb, bool Explorer)> requests = new();
    private readonly CancellationTokenSource windowLifetime = new();
    private readonly System.Windows.Threading.DispatcherTimer playlistTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private Task? playlistRefresh;
    private bool playlistConnected, previewMode;
    private string connectionProblem = "";
    private const int PageSize = 48;
    public bool Busy => operation != null;
    public MainWindow()
    {
        InitializeComponent(); FolderText.Text = Settings.MediaRoot; Closing += ClosingWindow; UpdateMonitorText();
        Loaded += (_, _) => { if (!previewMode) { _ = RefreshAvailablePlaylists(); playlistTimer.Start(); } };
        playlistTimer.Tick += (_, _) => { if (!Busy && !previewMode && !PlaylistBox.IsDropDownOpen) _ = RefreshAvailablePlaylists(); };
    }
    private void UpdateMonitorText() => MonitorText.Text = Settings.MonitorUsb ? "USB monitoring is on • Runs quietly in your system tray" : "USB monitoring is off • Enable it in Settings";
    public void ShowApp() { Show(); WindowState = WindowState.Normal; Activate(); }
    public void ExitApp() { exiting = true; operation?.Cancel(); previews?.Cancel(); Close(); }
    private void ClosingWindow(object? sender, CancelEventArgs e) { if (!exiting) { e.Cancel = true; Hide(); return; } playlistTimer.Stop(); windowLifetime.Cancel(); _ = previewTask.ContinueWith(_ => media.Dispose()); }
    private void View(string value)
    {
        screen = value;
        foreach (var pair in new[] { (WelcomePanel, "welcome"), (SelectionPanel as FrameworkElement, "selection"), (ReviewPanel, "review"), (BusyPanel, "busy"), (DonePanel, "done") }) pair.Item1.Visibility = pair.Item2 == value ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = value is "selection" or "review" or "busy" ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Content = value == "busy" ? "Cancel" : "Back";
        NextButton.Visibility = value is "selection" or "review" or "done" ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = value == "review" ? "Import into ProPresenter" : value == "done" ? "Choose more files" : "Review selection →";
        UpdateSelection();
    }
    private IProgress<WorkProgress> Reporter() => new Progress<WorkProgress>(p => { BusyText.Text = p.Text; StatusText.Text = p.Text; Progress.IsIndeterminate = p.Fraction < 0; if (p.Fraction >= 0) Progress.Value = p.Fraction * 100; });
    private void Error(Exception ex) { StatusText.Text = ex is OperationCanceledException ? "Cancelled. Start again or resume a prepared import." : ex.Message; if (ex is not OperationCanceledException) MessageBox.Show(this, ex.Message, "Import needs attention", MessageBoxButton.OK, MessageBoxImage.Information); }
    private async void FilesClick(object sender, RoutedEventArgs e) { var d = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Choose images, videos or archives", Filter = "All files|*.*" }; if (d.ShowDialog(this) == true) await LoadSources(d.FileNames, false); }
    private async void FolderClick(object sender, RoutedEventArgs e) { var d = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a media folder" }; if (d.ShowDialog(this) == true) await LoadSources([d.FolderName], false); }
    private async void WindowDrop(object sender, System.Windows.DragEventArgs e) { if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths) await Receive(paths, false); }
    public async Task Receive(string[] paths, bool usb, bool explorer = false)
    {
        if (!IsEnabled || Busy || screen is "selection" or "review") { requests.Enqueue((paths, usb, explorer)); StatusText.Text = "Another source is waiting. Finish this selection to continue."; return; }
        ShowApp(); await LoadSources(paths, usb, explorer);
    }
    private async Task StopPreviews() { previews?.Cancel(); try { await previewTask; } catch { } previews?.Dispose(); previews = null; }
    private async Task LoadSources(string[] paths, bool ask, bool explorer = false)
    {
        if (Busy) return; await StopPreviews(); media.Dispose(); media = new(); operation = new(); var ct = operation.Token; View("busy"); BusyTitle.Text = "Finding your media…";
        try
        {
            var scanProgress = Reporter();
            items = await Task.Run(() => ExplorerSelection.Scan(media, paths, explorer, scanProgress, ct), ct);
            if (items.Count == 0) { View("welcome"); StatusText.Text = "No images or videos found."; if (media.Warnings.Count > 0) ShowWarnings(); return; }
            if (ask && MessageBox.Show(this, $"Found {items.Count} images and videos on {paths[0]}. Import into ProPresenter?", "USB media found", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { items = []; View("welcome"); return; }
            BusyTitle.Text = "Connecting to ProPresenter…";
            try { await Connect(ct); } catch (Exception ex) when (ex is not OperationCanceledException) { Error(ex); }
            page = 0; SearchBox.Text = ""; FilterBox.SelectedIndex = 0; View("selection"); RefreshPage(); StatusText.Text = $"Found {items.Count} media items.";
            if (media.Warnings.Count > 0) ShowWarnings(); StartPreviews();
        }
        catch (Exception ex) { View("welcome"); Error(ex); }
        finally { operation.Dispose(); operation = null; Drain(); }
    }
    private void ShowWarnings() { Directory.CreateDirectory(Settings.DataRoot); string path = Path.Combine(Settings.DataRoot, "last-scan.txt"); File.WriteAllLines(path, media.Warnings); MessageBox.Show(this, string.Join("\n", media.Warnings.Take(8)) + (media.Warnings.Count > 8 ? $"\nFull report: {path}" : ""), "Scan details"); }
    private void StartPreviews()
    {
        previews = new(); var ct = previews.Token;
        previewTask = Task.Run(async () => { foreach (var item in items.Where(i => !i.Imported)) { if (ct.IsCancellationRequested) break; try { var result = await media.Thumbnail(item, ct); await Dispatcher.InvokeAsync(() => { item.Thumbnail = result.Image; item.Detail = result.Detail; }); } catch (OperationCanceledException) { break; } catch (Exception ex) { await Dispatcher.InvokeAsync(() => item.Detail = "Preview unavailable: " + ex.Message); } } }, ct);
    }
    private async Task Connect(CancellationToken ct)
    {
        using var client = new ProPresenterClient(Settings); await client.EnsureRunning(Reporter(), ct);
        await RefreshAvailablePlaylists();
        if (!playlistConnected) throw new IOException(connectionProblem);
    }
    public Task RefreshAvailablePlaylists() => playlistRefresh ??= RefreshPlaylistCore();
    private async Task RefreshPlaylistCore()
    {
        await Task.Yield();
        int port = Settings.Port;
        try
        {
            if (PlaylistBox.Items.Count == 0) ConnectionText.Text = $"Loading playlists from port {port}…";
            var status = await ApiDiscovery.Detect(Settings, windowLifetime.Token);
            if (port != Settings.Port) return;
            if (!status.Running || status.ActivePort == null) throw new IOException(status.Message);
            if (Settings.AutoDetectApi && port != status.ActivePort.Value) { Settings.Port = status.ActivePort.Value; port = Settings.Port; Settings.Save(); }
            using var client = new ProPresenterClient(Settings);
            var playlists = await client.Playlists(windowLifetime.Token);
            if (port != Settings.Port) return;
            string selected = (PlaylistBox.SelectedItem as Playlist)?.Id ?? Settings.LastPlaylist;
            var existing = PlaylistBox.ItemsSource as List<Playlist>;
            if (existing == null || !existing.SequenceEqual(playlists))
            {
                PlaylistBox.ItemsSource = playlists;
                PlaylistBox.SelectedItem = playlists.FirstOrDefault(p => p.Id == selected) ?? playlists.FirstOrDefault();
            }
            playlistConnected = true; connectionProblem = "";
            ConnectionText.Text = playlists.Count == 0 ? "Connected • Create a playlist in ProPresenter; it will appear here automatically." : $"Connected • {playlists.Count} playlists • Port {port}";
            if (screen == "welcome") StatusText.Text = playlists.Count == 0 ? "No presentation playlists found." : "Playlists loaded. Choose files or connect a USB drive.";
        }
        catch (OperationCanceledException) when (windowLifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (port != Settings.Port) return;
            playlistConnected = false;
            connectionProblem = ex is IOException ? ex.Message : $"Cannot load playlists from ProPresenter on port {port}. {ex.Message}";
            ConnectionText.Text = connectionProblem;
            PlaylistBox.IsDropDownOpen = false;
        }
        finally { playlistRefresh = null; UpdateSelection(); }
    }
    public IReadOnlyList<Playlist> DisplayedPlaylists => PlaylistBox.Items.OfType<Playlist>().ToList();
    private async void PlaylistOpened(object? sender, EventArgs e) { if (!previewMode) await RefreshAvailablePlaylists(); }
    private IEnumerable<MediaItem> VisibleItems() => items.Where(i => !i.Imported && (FilterBox.SelectedIndex == 0 || i.IsVideo == (FilterBox.SelectedIndex == 2)) && (string.IsNullOrWhiteSpace(SearchBox.Text) || (i.Name + " " + i.Location).Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase)));
    private void RefreshPage() { if (MediaGrid == null) return; var filtered = VisibleItems().ToList(); int pages = Math.Max(1, (filtered.Count + PageSize - 1) / PageSize); page = Math.Clamp(page, 0, pages - 1); MediaGrid.ItemsSource = filtered.Skip(page * PageSize).Take(PageSize).ToList(); PageText.Text = $"Page {page + 1} of {pages}"; UpdateSelection(); }
    private void UpdateSelection() { if (SelectionText == null || NextButton == null) return; PlaylistBox.IsEnabled = playlistConnected && screen != "busy"; int count = items.Count(i => i.Selected && !i.Imported); SelectionText.Text = $"{count} selected • {items.Count(i => !i.Imported)} remaining • Filename order"; NextButton.IsEnabled = screen == "done" || (playlistConnected && count > 0 && PlaylistBox.SelectedItem != null && (screen != "review" || !string.IsNullOrWhiteSpace(NameBox.Text))); }
    private void SelectionChanged(object sender, RoutedEventArgs e) => Dispatcher.BeginInvoke(UpdateSelection);
    private void PlaylistChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();
    private void NameChanged(object sender, TextChangedEventArgs e) => UpdateSelection();
    private void SearchChanged(object sender, TextChangedEventArgs e) { page = 0; if (FilterBox != null) RefreshPage(); }
    private void FilterChanged(object sender, SelectionChangedEventArgs e) { page = 0; if (SearchBox != null) RefreshPage(); }
    private void SelectAllClick(object sender, RoutedEventArgs e) { foreach (var i in VisibleItems()) i.Selected = true; UpdateSelection(); }
    private void ClearClick(object sender, RoutedEventArgs e) { foreach (var i in items) i.Selected = false; UpdateSelection(); }
    private void PreviousClick(object sender, RoutedEventArgs e) { page--; RefreshPage(); }
    private void NextPageClick(object sender, RoutedEventArgs e) { page++; RefreshPage(); }
    private void BackClick(object sender, RoutedEventArgs e) { if (screen == "busy") { operation?.Cancel(); return; } View(screen == "review" ? "selection" : "welcome"); Drain(); }
    private async void NextClick(object sender, RoutedEventArgs e)
    {
        if (screen == "done") { View("welcome"); Drain(); return; }
        if (screen == "selection") { var selected = items.Where(i => i.Selected && !i.Imported).ToList(); var groups = selected.Select(i => i.Group).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); NameBox.Text = groups.Count == 1 ? (Directory.Exists(groups[0]) ? new DirectoryInfo(groups[0]).Name : Path.GetFileNameWithoutExtension(groups[0])) : "Imported media"; ReviewText.Text = $"{selected.Count} media items\nPlaylist: {PlaylistBox.SelectedItem}\n\n{selected.Count(i => !i.IsVideo)} images • {selected.Count(i => i.IsVideo)} videos"; View("review"); NameBox.Focus(); NameBox.SelectAll(); return; }
        if (screen == "review") await Import();
    }
    private async Task Import()
    {
        if (Busy || PlaylistBox.SelectedItem is not Playlist playlist) return; await StopPreviews(); operation = new(); var ct = operation.Token;
        var selected = items.Where(i => i.Selected && !i.Imported).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Key, StringComparer.Ordinal).ToList();
        var job = new ImportJob { Name = NameBox.Text.Trim(), PlaylistId = playlist.Id, Sources = selected.Select(i => i.Key).ToList(), Count = selected.Count };
        job.Folder = Path.Combine(Settings.MediaRoot, MediaServices.CleanName(job.Name) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + job.Id[..6]); job.PresentationFile = Path.Combine(job.Folder, MediaServices.CleanName(job.Name) + ".pro"); View("busy"); BusyTitle.Text = "Importing your media…";
        try
        {
            Directory.CreateDirectory(job.Folder); job.Save(); var prepared = new List<PreparedMedia>();
            for (int i = 0; i < selected.Count; i++) { int index = i; var reporter = new Progress<WorkProgress>(p => { BusyText.Text = $"{index + 1} of {selected.Count} • {p.Text}"; StatusText.Text = BusyText.Text; Progress.IsIndeterminate = p.Fraction < 0; if (p.Fraction >= 0) Progress.Value = 80 * (index + p.Fraction) / selected.Count; }); prepared.Add(await Task.Run(() => media.Prepare(selected[index], job.Folder, index, reporter, ct), ct)); }
            ct.ThrowIfCancellationRequested(); await File.WriteAllBytesAsync(job.PresentationFile, NativePresentation.Create(job.Name, job.PresentationId, prepared), ct); File.WriteAllText(Path.Combine(job.Folder, "media-manifest.json"), System.Text.Json.JsonSerializer.Serialize(prepared)); job.State = "Prepared"; job.Save();
            using var client = new ProPresenterClient(Settings); await client.EnsureRunning(Reporter(), ct); await client.Complete(job, Reporter(), ct);
            foreach (var item in selected) { item.Imported = true; item.Selected = false; } Settings.LastPlaylist = playlist.Id; Settings.Save(); lastFolder = job.Folder; Finish(job);
        }
        catch (Exception ex) { job.Error = ex.Message; job.Save(); View("review"); Error(ex); }
        finally { operation.Dispose(); operation = null; Drain(); }
    }
    private void Finish(ImportJob job)
    {
        DoneText.Text = $"“{job.Name}” has {job.Count} slides and is linked to your playlist.\n\nAll media is saved on this computer."; StatusText.Text = "Import verified. You can remove the USB drive."; View("done");
        int remaining = items.Count(i => !i.Imported); if (remaining > 0 && MessageBox.Show(this, $"{remaining} media items remain. Add another playlist item?", "Create another item", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) { foreach (var item in items.Where(i => !i.Imported)) item.Selected = true; page = 0; View("selection"); RefreshPage(); StartPreviews(); }
    }
    private async void ResumeClick(object sender, RoutedEventArgs e)
    {
        if (Busy) return; var pending = ImportJob.Pending().OrderByDescending(j => File.GetLastWriteTime(j.PresentationFile)).ToList(); if (pending.Count == 0) { MessageBox.Show(this, "No prepared imports need to be resumed.", "Resume import"); return; }
        var job = Dialogs.ChooseJob(this, pending); if (job == null) return; await StopPreviews(); operation = new(); var ct = operation.Token; View("busy"); BusyTitle.Text = "Resuming your import…";
        try { using var client = new ProPresenterClient(Settings); await client.EnsureRunning(Reporter(), ct); await client.Complete(job, Reporter(), ct); foreach (var item in items.Where(i => job.Sources.Contains(i.Key))) { item.Imported = true; item.Selected = false; } lastFolder = job.Folder; Finish(job); }
        catch (Exception ex) { job.Error = ex.Message; job.Save(); View(items.Count > 0 ? "selection" : "welcome"); Error(ex); }
        finally { operation.Dispose(); operation = null; Drain(); }
    }
    private void OpenFolderClick(object sender, RoutedEventArgs e) { if (lastFolder != null) Process.Start(new ProcessStartInfo(lastFolder) { UseShellExecute = true }); }
    private void SettingsClick(object sender, RoutedEventArgs e) { if (Busy) return; if (Dialogs.EditSettings(this, Settings)) { Settings.Save(); FolderText.Text = Settings.MediaRoot; playlistConnected = false; PlaylistBox.ItemsSource = null; UpdateSelection(); UpdateMonitorText(); _ = RefreshAvailablePlaylists(); } }
    private void Drain() { if (!Busy && (screen is "welcome" or "done") && requests.Count > 0) { var r = requests.Dequeue(); _ = Receive(r.Paths, r.Usb, r.Explorer); } }
    public void Demo()
    {
        previewMode = true; playlistConnected = true;
        PlaylistBox.ItemsSource = new[] { new Playlist("demo", "Sunday Service / Morning") }; PlaylistBox.SelectedIndex = 0; ConnectionText.Text = "Preview mode";
        for (int i = 0; i < 9; i++) { var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) { dc.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(45 + i * 12), (byte)(35 + i * 9), (byte)(80 + i * 12))), null, new Rect(0, 0, 320, 180)); dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(140, 115, 240)), null, new System.Windows.Point(240 - i * 8, 80), 55, 55); } var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(320, 180, 96, 96, PixelFormats.Pbgra32); bmp.Render(visual); bmp.Freeze(); items.Add(new MediaItem { Source = "C:\\USB\\Sunday\\" + i + ".png", Name = $"{i + 1:00} - {(i == 3 ? "Welcome video.mp4" : "Service slide.png")}", Group = "Sunday", IsVideo = i == 3, Thumbnail = bmp, Detail = i == 3 ? "0:42 • 1920 × 1080" : "1920 × 1080" }); }
        View("selection"); RefreshPage();
    }
}
