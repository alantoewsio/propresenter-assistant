using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32;
using ProPresenterAssistant.Updates;

namespace ProPresenterAssistant.Setup;

public static partial class Program
{
    private static string Argument(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : throw new IOException("Missing update argument: " + name);
    }
    private static async Task<int> SilentUpdate(string[] args)
    {
        FileStream? gate = null; Process? launched = null; InstallTransaction? transaction = null;
        bool committed = false, replaced = false;
        string work = Path.Combine(UpdateSafety.Root, "install-" + Guid.NewGuid().ToString("N"));
        StreamWriter? reply = null;
        try
        {
            string token = Argument(args, "--handoff");
            if (!Guid.TryParseExact(token, "N", out _)) throw new IOException("Invalid update handoff.");
            using var parent = Process.GetProcessById(int.Parse(Argument(args, "--parent")));
            if (parent.StartTime.ToUniversalTime().Ticks != long.Parse(Argument(args, "--parent-start")) ||
                !string.Equals(parent.MainModule?.FileName, Executable, StringComparison.OrdinalIgnoreCase)) throw new IOException("The update did not originate from the installed assistant.");
            if (string.Equals(Path.GetDirectoryName(Environment.ProcessPath), InstallRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("Run the updater outside the installation folder.");
            gate = UpdateSafety.AcquireLock();
            string stage = Path.Combine(work, "stage"), backup = Path.Combine(work, "backup"); Directory.CreateDirectory(stage);
            using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip") ?? throw new IOException("Missing payload."))
            using (var zip = new ZipArchive(payload))
                foreach (var entry in zip.Entries)
                {
                    string path = UpdateSafety.SafePath(stage, entry.FullName); if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!); entry.ExtractToFile(path);
                }
            foreach (string name in new[] { "ProPresenterAssistant.exe", "ProPresenterAssistant.Shell.msix", "ProPresenterAssistant.Shell.cer", "Assets/Logo.png" })
                if (!File.Exists(Path.Combine(stage, name))) throw new IOException("Incomplete update payload.");
            if (!Directory.GetFiles(stage, "ProPresenterAssistant.Shell-*.dll").Any()) throw new IOException("Missing context-menu extension.");
            using (var cert = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(stage, "ProPresenterAssistant.Shell.cer")))
            using (var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine))
            {
                store.Open(OpenFlags.ReadOnly);
                if (store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).Count == 0)
                    throw new IOException("This update needs Windows approval for its context-menu certificate. Run the downloaded installer interactively instead.");
            }
            File.Copy(Environment.ProcessPath!, Path.Combine(stage, "Uninstall.exe"));
            string version = Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);
            if (UpdateSafety.ParseVersion(FileVersionInfo.GetVersionInfo(Path.Combine(stage, "ProPresenterAssistant.exe")).ProductVersion ?? "") != UpdateSafety.ParseVersion(version)) throw new IOException("The installer payload version does not match the update.");
            using var connection = new NamedPipeServerStream("PPA-update-" + token, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var handoffTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await connection.WaitForConnectionAsync(handoffTimeout.Token);
            using var reader = new StreamReader(connection, Encoding.UTF8, false, 1024, true);
            reply = new StreamWriter(connection, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            await reply.WriteLineAsync("READY " + version);
            if (await reader.ReadLineAsync(handoffTimeout.Token) != "COMMIT") throw new IOException("Update cancelled before shutdown.");
            committed = true;
            await reply.WriteLineAsync("COMMITTED");
            using (var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45))) await parent.WaitForExitAsync(exitTimeout.Token);
            transaction = new InstallTransaction(stage, InstallRoot, backup);
            transaction.Apply(); replaced = true;
            ShellRegistration.Install(InstallRoot);
            using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey, true)) key?.SetValue("DisplayVersion", version);
            string restart = Guid.NewGuid().ToString("N");
            using var health = new NamedPipeServerStream("PPA-restart-" + restart, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo(Executable) { UseShellExecute = true, WorkingDirectory = InstallRoot };
            start.ArgumentList.Add("--update-started"); start.ArgumentList.Add(restart);
            launched = Process.Start(start) ?? throw new IOException("The updated assistant could not start.");
            using var healthTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var connected = health.WaitForConnectionAsync(healthTimeout.Token);
            if (await Task.WhenAny(connected, launched.WaitForExitAsync(healthTimeout.Token)) != connected) throw new IOException("The updated assistant exited before confirming startup.");
            await connected;
            using var healthReader = new StreamReader(health, Encoding.UTF8, false, 1024, true);
            using var healthWriter = new StreamWriter(health, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            await healthWriter.WriteLineAsync("START " + version);
            if (await healthReader.ReadLineAsync(healthTimeout.Token) != "HEALTHY " + version) throw new IOException("The new assistant did not confirm successful startup.");
            File.WriteAllText(Path.Combine(UpdateSafety.Root, "last-result.txt"), "Updated to " + version);
            gate.Dispose(); gate = null;
            await healthWriter.WriteLineAsync("SUCCESS");
            try { Directory.Delete(work, true); } catch { }
            return 0;
        }
        catch (Exception ex)
        {
            string error = "The update could not be completed: " + ex.Message;
            try
            {
                if (launched != null && !launched.HasExited) { launched.Kill(); await launched.WaitForExitAsync(); }
                if (transaction != null)
                {
                    transaction.Rollback();
                    if (replaced) ShellRegistration.Install(InstallRoot);
                    using var key = Registry.CurrentUser.OpenSubKey(UninstallKey, true);
                    key?.SetValue("DisplayVersion", UpdateSafety.ParseVersion(FileVersionInfo.GetVersionInfo(Executable).ProductVersion ?? "")?.ToString(3) ?? "unknown");
                }
            }
            catch (Exception recovery) { error += " Recovery needs attention: " + recovery.Message + ". Backup: " + Path.Combine(work, "backup"); }
            Directory.CreateDirectory(UpdateSafety.Root); File.WriteAllText(Path.Combine(UpdateSafety.Root, "last-result.txt"), error);
            try { if (reply != null) await reply.WriteLineAsync("ERROR " + error); } catch { }
            gate?.Dispose(); gate = null;
            // Before COMMIT the old app is still running. After COMMIT a detached installer owns recovery.
            if (committed)
            {
                if (!Process.GetProcessesByName("ProPresenterAssistant").Any(p => { try { return string.Equals(p.MainModule?.FileName, Executable, StringComparison.OrdinalIgnoreCase); } catch { return false; } }))
                {
                    try { var start = new ProcessStartInfo(Executable) { UseShellExecute = true }; start.ArgumentList.Add("--update-recovered"); Process.Start(start); }
                    catch { System.Windows.MessageBox.Show(error, "Assistant update needs attention"); }
                }
                else System.Windows.MessageBox.Show(error, "Assistant update needs attention");
            }
            return 1;
        }
        finally { gate?.Dispose(); launched?.Dispose(); }
    }
}
