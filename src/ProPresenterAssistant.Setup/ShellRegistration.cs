using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;

namespace ProPresenterAssistant.Setup;
internal static class ShellRegistration
{
    public const string PackageName = "ProPresenterAssistant.Shell";
    public static void TrustBundledCertificate()
    {
        // Elevation trusts only the public certificate embedded in this installer.
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip") ?? throw new IOException("Missing installer payload.");
        using var zip = new ZipArchive(payload);
        using var stream = (zip.GetEntry("ProPresenterAssistant.Shell.cer") ?? throw new IOException("Missing shell certificate.")).Open();
        using var buffer = new MemoryStream(); stream.CopyTo(buffer);
        using var certificate = X509CertificateLoader.LoadCertificate(buffer.ToArray());
        if (certificate.Subject != "CN=ProPresenter Assistant") throw new IOException("Unexpected shell signing certificate.");
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite); store.Add(certificate);
    }
    public static void Install(string root)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) throw new IOException("The modern context menu requires Windows 11.");
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(root, "ProPresenterAssistant.Shell.cer"));
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        if (store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false).Count == 0)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            start.ArgumentList.Add("--trust-shell-certificate");
            using var process = Process.Start(start) ?? throw new IOException("Certificate registration did not start.");
            process.WaitForExit(); if (process.ExitCode != 0) throw new IOException("Windows did not approve the context-menu certificate. Retry Install to enable it.");
        }
        RunPowerShell($"Add-AppxPackage -Path {Quote(Path.Combine(root, "ProPresenterAssistant.Shell.msix"))} -ExternalLocation {Quote(root)} -ForceUpdateFromAnyVersion -ErrorAction Stop");
    }
    public static void Remove() => RunPowerShell($"Get-AppxPackage -Name '{PackageName}' | Remove-AppxPackage -ErrorAction Stop");
    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    private static void RunPowerShell(string script)
    {
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop'; " + script)) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Windows package registration did not start.");
        var error = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
        process.WaitForExit(); Task.WaitAll(error, output);
        if (process.ExitCode != 0) throw new IOException("Windows context-menu registration failed: " + error.Result);
    }
}
