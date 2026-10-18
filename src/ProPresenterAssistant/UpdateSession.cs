using System.IO;
using System.Text.Json;
using ProPresenterAssistant.Updates;

namespace ProPresenterAssistant;

public partial class MainWindow
{
    private record SavedItem(string Source, string? Entry, string Name, string Group, long Size, bool Video, bool Selected, bool Imported);
    private record SavedSession(List<SavedItem> Items, string Playlist, string Name, string Search, int Filter, bool Review, List<QueuedSource> Queue);
    private record QueuedSource(string[] Paths, bool Usb, bool Explorer);
    private static string SessionFile => Path.Combine(UpdateSafety.Root, "session.json");
    public async Task SaveUpdateSession()
    {
        if (Busy) throw new IOException("Finish the active import before updating.");
        await StopPreviews();
        var saved = new SavedSession(items.Select(i => new SavedItem(i.Source, i.Entry, i.Name, i.Group, i.Size, i.IsVideo, i.Selected, i.Imported)).ToList(),
            (PlaylistBox.SelectedItem as Playlist)?.Id ?? Settings.LastPlaylist, NameBox.Text, SearchBox.Text, FilterBox.SelectedIndex, screen == "review", requests.Select(r => new QueuedSource(r.Paths, r.Usb, r.Explorer)).ToList());
        Directory.CreateDirectory(UpdateSafety.Root);
        File.WriteAllText(SessionFile + ".tmp", JsonSerializer.Serialize(saved)); File.Move(SessionFile + ".tmp", SessionFile, true);
        Settings.Save();
    }
    public async Task RestoreUpdateSession()
    {
        if (!File.Exists(SessionFile)) return;
        var saved = JsonSerializer.Deserialize<SavedSession>(File.ReadAllText(SessionFile)) ?? throw new IOException("The saved update session could not be read.");
        items = saved.Items.Select(i => new MediaItem { Source = i.Source, Entry = i.Entry, Name = i.Name, Group = i.Group, Size = i.Size, IsVideo = i.Video, Selected = i.Selected, Imported = i.Imported }).ToList();
        foreach (var queued in saved.Queue) requests.Enqueue((queued.Paths, queued.Usb, queued.Explorer));
        Settings.LastPlaylist = saved.Playlist;
        await RefreshAvailablePlaylists();
        SearchBox.Text = saved.Search; FilterBox.SelectedIndex = saved.Filter; NameBox.Text = saved.Name;
        if (items.Count > 0)
        {
            View(saved.Review ? "review" : "selection"); RefreshPage();
            ReviewText.Text = $"{items.Count(i => i.Selected && !i.Imported)} media items\nPlaylist: {PlaylistBox.SelectedItem}";
            StartPreviews();
            if (items.Any(i => !File.Exists(i.Source))) StatusText.Text = "Your selection was restored. Reconnect its USB drive before importing.";
        }
    }
    public void FinishUpdateSession() { if (File.Exists(SessionFile)) File.Delete(SessionFile); }
    public void ResumeUpdatePreviews() { if (previews == null && items.Count > 0 && !Busy) StartPreviews(); }
}
