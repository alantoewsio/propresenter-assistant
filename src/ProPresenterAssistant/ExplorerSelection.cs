using System.IO;

namespace ProPresenterAssistant;
public record IncomingImport(string[] Paths, bool Explorer = false);
public static class ExplorerSelection
{
    public static List<MediaItem> Scan(MediaServices service, string[] paths, bool explorer, IProgress<WorkProgress> progress, CancellationToken ct)
    {
        if (!explorer) return service.Scan(paths, progress, ct);
        var explicitPaths = paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = explicitPaths.Where(Directory.Exists).ToArray();
        bool InSelectedFolder(string file) => folders.Any(folder => Path.GetFullPath(file).StartsWith(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        var roots = explicitPaths.Select(path => File.Exists(path) && MediaServices.IsImage(path) ? Path.GetDirectoryName(path)! : path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Browse neighboring pictures without opening unrelated archives or adding unselected videos.
        var items = service.Scan(roots, progress, ct, file => explicitPaths.Contains(Path.GetFullPath(file)) || InSelectedFolder(file) || MediaServices.IsImage(file));
        foreach (var item in items) item.Selected = explicitPaths.Contains(Path.GetFullPath(item.Source)) || InSelectedFolder(item.Source);
        return items;
    }
}
