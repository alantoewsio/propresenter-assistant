using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace ProPresenterAssistant.Setup;
public static class Program
{
    private static readonly string InstallRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ProPresenter Assistant");
    private static string Executable => Path.Combine(InstallRoot, "ProPresenterAssistant.exe");
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ProPresenterAssistant";
    private static readonly string[] Links = [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ProPresenter Assistant.lnk"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ProPresenter Assistant.lnk"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "ProPresenter.lnk")];
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--trust-shell-certificate")) { try { ShellRegistration.TrustBundledCertificate(); return 0; } catch { return 1; } }
        if (args.Contains("--uninstall") && string.Equals(Path.GetDirectoryName(Environment.ProcessPath), InstallRoot, StringComparison.OrdinalIgnoreCase))
        {
            string temporary = Path.Combine(Path.GetTempPath(), "ProPresenterAssistant-uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(Environment.ProcessPath!, temporary);
            var start = new ProcessStartInfo(temporary) { UseShellExecute = true };
            start.ArgumentList.Add("--uninstall"); start.ArgumentList.Add("--wait"); start.ArgumentList.Add(Environment.ProcessId.ToString());
            Process.Start(start); return 0;
        }
        int waitAt = Array.IndexOf(args, "--wait");
        if (waitAt >= 0 && waitAt + 1 < args.Length && int.TryParse(args[waitAt + 1], out int parentId)) { try { Process.GetProcessById(parentId).WaitForExit(10000); } catch (ArgumentException) { } }
        if (args.Contains("--verify-payload"))
        {
            try { using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip") ?? throw new Exception("Missing payload"); using var zip = new ZipArchive(resource); foreach (string required in new[] { "ProPresenterAssistant.exe", "README.md", "ProPresenterAssistant.Shell.dll", "ProPresenterAssistant.Shell.msix", "ProPresenterAssistant.Shell.cer", "Assets/Logo.png" }) if (zip.GetEntry(required) == null && zip.GetEntry(required.Replace('/', '\\')) == null) throw new Exception("Incomplete payload: " + required); foreach (var e in zip.Entries) ValidateEntry(e.FullName); File.WriteAllText(args.Last(), $"Verified {zip.Entries.Count} installer payload entries, including the native Windows 11 context menu package."); return 0; } catch (Exception ex) { File.WriteAllText(args.Last(), ex.ToString()); return 1; }
        }
        bool uninstall = args.Contains("--uninstall"); var app = new Application();
        var window = new Window { Title = uninstall ? "Uninstall ProPresenter Assistant" : "Install ProPresenter Assistant", Width = 620, Height = 520, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = new SolidColorBrush(Color.FromRgb(16, 19, 27)), Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontSize = 14 };
        var panel = new StackPanel { Margin = new Thickness(32) }; window.Content = panel;
        window.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app-icon.ico"));
        panel.Children.Add(new TextBlock { Text = "PROPRESENTER ASSISTANT", Foreground = new SolidColorBrush(Color.FromRgb(161, 139, 255)), FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = uninstall ? "Remove the assistant." : "Make media imports easier.", FontSize = 27, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 18) });
        panel.Children.Add(new TextBlock { Text = uninstall ? "Removes the app, context menu, startup entry and shortcuts. Your imported media and import records will be kept." : "Installs for your Windows account, with bundled tools and an Import into ProPresenter entry in Windows 11’s first right-click menu. Windows will ask for administrator approval to trust this build’s context-menu signing certificate on first installation.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = InstallRoot, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, Margin = new Thickness(0, 18, 0, 18) });
        var desktop = new CheckBox { Content = "Create a desktop shortcut", Foreground = Brushes.White, IsChecked = true, Margin = new Thickness(0, 5, 0, 5) };
        var startup = new CheckBox { Content = "Start quietly when I sign into Windows", Foreground = Brushes.White, IsChecked = true, Margin = new Thickness(0, 5, 0, 15) };
        if (!uninstall) { panel.Children.Add(desktop); panel.Children.Add(startup); }
        var progress = new ProgressBar { Height = 8, Margin = new Thickness(0, 16, 0, 14), Visibility = Visibility.Collapsed }; panel.Children.Add(progress);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray }; panel.Children.Add(status);
        var action = new Button { Content = uninstall ? "Uninstall" : "Install", Padding = new Thickness(24, 10, 24, 10), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) }; panel.Children.Add(action);
        bool working = false;
        window.Closing += (_, e) => { if (working) e.Cancel = true; };
        action.Click += async (_, _) =>
        {
            if (action.Content?.ToString() == "Open assistant") { Process.Start(new ProcessStartInfo(Executable) { UseShellExecute = true }); window.Close(); return; }
            if (action.Content?.ToString() == "Close") { window.Close(); return; }
            working = true; action.IsEnabled = false; progress.Visibility = Visibility.Visible; progress.IsIndeterminate = true;
            try
            {
                if (Process.GetProcessesByName("ProPresenterAssistant").Any()) throw new IOException("Exit ProPresenter Assistant from its system tray menu, then retry.");
                if (uninstall) { await Task.Run(Uninstall); status.Text = "Uninstalled. Your imported media has been kept."; action.Content = "Close"; }
                else
                {
                    status.Text = "Extracting the app and bundled tools…"; await Task.Run(Install);
                    status.Text = "Registering the Windows context menu…"; await Task.Run(() => ShellRegistration.Install(InstallRoot));
                    CreateShortcut(Links[1], ""); if (File.Exists(Links[2])) File.Delete(Links[2]); if (desktop.IsChecked == true) CreateShortcut(Links[0], "");
                    using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); if (startup.IsChecked == true) run.SetValue("ProPresenterAssistant", $"\"{Executable}\" --tray"); else run.DeleteValue("ProPresenterAssistant", false);
                    status.Text = "Installed. Enable ProPresenter’s local API in its Network settings, then open the assistant."; action.Content = "Open assistant";
                }
                progress.IsIndeterminate = false; progress.Value = 100;
            }
            catch (Exception ex) { status.Text = ex.Message; progress.Visibility = Visibility.Collapsed; }
            finally { working = false; action.IsEnabled = true; }
        };
        int screenshotAt = Array.IndexOf(args, "--screenshot");
        if (screenshotAt >= 0 && screenshotAt + 1 < args.Length)
        {
            window.Loaded += async (_, _) => { await Task.Delay(300); window.UpdateLayout(); var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window); var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap)); using var stream = File.Create(args[screenshotAt + 1]); png.Save(stream); app.Shutdown(); };
        }
        return app.Run(window);
    }
    private static void ValidateEntry(string name) { if (Path.IsPathRooted(name) || name.Contains(':') || name.Replace('\\', '/').Split('/').Contains("..")) throw new IOException("Unsafe installer payload."); }
    private static void Install()
    {
        Directory.CreateDirectory(InstallRoot);
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip") ?? throw new IOException("Installer payload is missing."); using var zip = new ZipArchive(payload);
        foreach (var entry in zip.Entries) { ValidateEntry(entry.FullName); string path = Path.GetFullPath(Path.Combine(InstallRoot, entry.FullName)); if (!path.StartsWith(InstallRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid install path."); if (entry.FullName.EndsWith('/')) Directory.CreateDirectory(path); else { Directory.CreateDirectory(Path.GetDirectoryName(path)!); entry.ExtractToFile(path, true); } }
        string uninstall = Path.Combine(InstallRoot, "Uninstall.exe"); if (!string.Equals(Environment.ProcessPath, uninstall, StringComparison.OrdinalIgnoreCase)) File.Copy(Environment.ProcessPath!, uninstall, true);
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey); key.SetValue("DisplayName", "ProPresenter Assistant"); key.SetValue("DisplayVersion", "0.1.0"); key.SetValue("Publisher", "ProPresenter Assistant Project"); key.SetValue("InstallLocation", InstallRoot); key.SetValue("UninstallString", $"\"{uninstall}\" --uninstall"); key.SetValue("DisplayIcon", Executable); key.SetValue("NoModify", 1); key.SetValue("NoRepair", 1);
    }
    private static void CreateShortcut(string path, string arguments)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); Type type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows shortcut service unavailable."); dynamic shell = Activator.CreateInstance(type)!; dynamic link = shell.CreateShortcut(path);
        try { link.TargetPath = Executable; link.Arguments = arguments; link.WorkingDirectory = InstallRoot; link.Description = "Import media into ProPresenter"; link.IconLocation = Executable + ",0"; link.Save(); } finally { Marshal.FinalReleaseComObject(link); Marshal.FinalReleaseComObject(shell); }
    }
    private static void Uninstall()
    {
        // Fixed, per-user install root only. Never delete imported media or user data.
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ProPresenter Assistant"); if (!string.Equals(Path.GetFullPath(InstallRoot), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid uninstall root.");
        ShellRegistration.Remove();
        foreach (string link in Links) { if (File.Exists(link)) File.Delete(link); }
        using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) run?.DeleteValue("ProPresenterAssistant", false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        if (Directory.Exists(InstallRoot)) Directory.Delete(InstallRoot, true);
    }
}
