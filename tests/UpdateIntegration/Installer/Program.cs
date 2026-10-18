using Microsoft.Win32;

namespace ProPresenterAssistant.Setup
{
    // Executes the production updater against disposable files. Only shell registration and the fixed install location are substituted.
    public static partial class Program
    {
        private static string InstallRoot => Environment.GetEnvironmentVariable("PPA_TEST_ROOT")!;
        private static string Executable => Path.Combine(InstallRoot, "ProPresenterAssistant.exe");
        private const string UninstallKey = @"Software\ProPresenterAssistant-UpdateIntegration-Temporary";
        public static int Main(string[] args) => SilentUpdate(args).GetAwaiter().GetResult();
    }
    internal static class ShellRegistration
    {
        public static void Install(string root)
        {
            if (Environment.GetEnvironmentVariable("PPA_TEST_MODE") == "registration-failure" && File.ReadAllText(Path.Combine(root, "version.txt")) == "new") throw new IOException("Injected registration failure");
        }
    }
}
namespace System.Windows
{
    public static class MessageBox { public static void Show(string text, string title) => File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("PPA_TEST_ROOT")!, "error.txt"), text); }
}
