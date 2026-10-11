using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Media;

namespace ProPresenterAssistant;

public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class MediaItem : Observable
{
    public required string Source { get; init; }
    public string? Entry { get; init; }
    public required string Name { get; init; }
    public required string Group { get; init; }
    public long Size { get; init; }
    public bool IsVideo { get; init; }
    public string Key => Source + "|" + Entry;
    public string Location => Entry == null ? Path.GetDirectoryName(Source)! : Path.GetFileName(Source) + " / " + Path.GetDirectoryName(Entry);
    public string Kind => IsVideo ? "VIDEO" : "IMAGE";
    private bool selected = true;
    public bool Selected { get => selected; set { selected = value; Changed(); } }
    private ImageSource? thumbnail;
    public ImageSource? Thumbnail { get => thumbnail; set { thumbnail = value; Changed(); } }
    private string detail = "Preparing preview…";
    public string Detail { get => detail; set { detail = value; Changed(); } }
    public string? PreviewFile { get; set; }
    public bool Imported { get; set; }
}

public record Playlist(string Id, string Name) { public override string ToString() => Name; }
public record WorkProgress(string Text, double Fraction);
public record PreparedMedia(string Path, string Name, bool Video, int Width, int Height, double Duration, double FrameRate);

public sealed class Settings
{
    public int Port { get; set; } = 50001;
    public bool AutoDetectApi { get; set; } = true;
    public string Executable { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Renewed Vision", "ProPresenter", "ProPresenter.exe");
    public string MediaRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ProPresenter Imported Media");
    public string LastPlaylist { get; set; } = "";
    public string LibraryFolder { get; set; } = "";
    public bool MonitorUsb { get; set; } = true;
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProPresenterAssistant");
    public static string FilePath => Path.Combine(DataRoot, "settings.json");
    public static Settings Load() { try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); } catch { return new(); } }
    public void Save() { Directory.CreateDirectory(DataRoot); File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
}

public sealed class ImportJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string PlaylistId { get; set; } = "";
    public string PresentationId { get; set; } = Guid.NewGuid().ToString();
    public string State { get; set; } = "Preparing";
    public string Folder { get; set; } = "";
    public string PresentationFile { get; set; } = "";
    public List<string> Sources { get; set; } = [];
    public int Count { get; set; }
    public string Error { get; set; } = "";
    public void Save()
    {
        Directory.CreateDirectory(Path.Combine(Settings.DataRoot, "jobs"));
        string file = Path.Combine(Settings.DataRoot, "jobs", Id + ".json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(file + ".tmp", file, true);
    }
    public static IEnumerable<ImportJob> Pending()
    {
        string dir = Path.Combine(Settings.DataRoot, "jobs");
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            ImportJob? j = null;
            try { j = JsonSerializer.Deserialize<ImportJob>(File.ReadAllText(file)); } catch { }
            if (j != null && j.State != "Complete" && File.Exists(j.PresentationFile)) yield return j;
        }
    }
}
