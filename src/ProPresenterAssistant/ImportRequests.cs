using System.IO;
using System.Text;

namespace ProPresenterAssistant;
public static class ImportRequests
{
    public static string[] ReadArguments(string[] args)
    {
        int at = Array.IndexOf(args, "--import-list");
        if (at < 0) return args.Where(arg => !arg.StartsWith("--") && (File.Exists(arg) || Directory.Exists(arg))).ToArray();
        if (at + 1 >= args.Length) throw new IOException("Missing Explorer import request.");
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ProPresenterAssistant-Requests"));
        string path = Path.GetFullPath(args[at + 1]);
        if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(Path.GetFileNameWithoutExtension(path), out _) || Path.GetExtension(path) != ".txt")
            throw new IOException("Invalid Explorer import request.");
        var info = new FileInfo(path);
        if (info.Length > 8 * 1024 * 1024 || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Invalid Explorer import request file.");
        try { return File.ReadAllLines(path, Encoding.Unicode).Where(p => File.Exists(p) || Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); }
        finally { File.Delete(path); }
    }
}
