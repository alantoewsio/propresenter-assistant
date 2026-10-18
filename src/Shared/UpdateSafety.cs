using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ProPresenterAssistant.Updates;

public static class UpdateSafety
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProPresenterAssistant", "updates");
    public static string LockPath => Path.Combine(Root, "install.lock");
    public static FileStream AcquireLock() { Directory.CreateDirectory(Root); return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
    public static bool Installing() { try { using var gate = AcquireLock(); return false; } catch (IOException) { return true; } }
    public static Version? ParseVersion(string value) => Version.TryParse(value.TrimStart('v', 'V').Split('+')[0], out var version) && !value.Contains('-') ? new Version(version.Major, version.Minor, Math.Max(0, version.Build)) : null;
    public static async Task Verify(string path, long size, string digest, CancellationToken ct)
    {
        if (new FileInfo(path).Length != size || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != 71) throw new IOException("The update's size or checksum is invalid.");
        using var stream = File.OpenRead(path);
        string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        if (!string.Equals(actual, digest[7..], StringComparison.OrdinalIgnoreCase)) throw new IOException("The update checksum does not match. Nothing was installed.");
    }
    public static string SafePath(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Replace('\\', '/').Split('/').Any(p => p is ".." or ".")) throw new IOException("Unsafe installer path.");
        string result = Path.GetFullPath(Path.Combine(root, relative));
        if (!result.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe installer path.");
        return result;
    }
    public static bool SameFile(string first, string second)
    {
        if (!File.Exists(second) || new FileInfo(first).Length != new FileInfo(second).Length) return false;
        using var a = File.OpenRead(first); using var b = File.OpenRead(second);
        return SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b));
    }
}

// Only payload-owned files are replaced. Settings, imported media and user choices are outside this transaction.
public sealed class InstallTransaction(string stage, string destination, string backup)
{
    private readonly List<(string Target, string? Backup)> changed = [];
    public void Apply()
    {
        foreach (string source in Directory.GetFiles(stage, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(stage, source), target = UpdateSafety.SafePath(destination, relative);
            if (UpdateSafety.SameFile(source, target)) continue;
            string? saved = null;
            if (File.Exists(target)) { saved = UpdateSafety.SafePath(backup, relative); Directory.CreateDirectory(Path.GetDirectoryName(saved)!); File.Copy(target, saved, false); }
            changed.Add((target, saved)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target, true);
        }
    }
    public void Rollback()
    {
        var errors = new List<Exception>();
        foreach (var item in changed.AsEnumerable().Reverse())
        {
            try
            {
                if (item.Backup != null)
                {
                    // A failed replacement may leave an unchanged, locked Explorer DLL. Do not rewrite it.
                    if (UpdateSafety.SameFile(item.Backup, item.Target)) continue;
                    File.Copy(item.Backup, item.Target, true);
                }
                else if (File.Exists(item.Target)) File.Delete(item.Target);
            }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (errors.Count > 0) throw new AggregateException("Some files could not be restored.", errors);
    }
}
