using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProPresenterAssistant;
public record NetworkPreferences(bool? Enabled, int? Port);
public record ApiStatus(bool Running, bool? Enabled, int? ConfiguredPort, int? ActivePort, string Message);
public static class ApiDiscovery
{
    public static NetworkPreferences ParsePreferences(string text)
    {
        string Value(string key) => Regex.Match(text, @"(?:^|[;\r\n])\s*" + Regex.Escape(key) + @"\s*=\s*([^;\r\n]*)", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
        bool? enabled = bool.TryParse(Value("EnableNetwork"), out bool flag) ? flag : null;
        int? port = int.TryParse(Value("NetworkPort"), out int number) && number is > 0 and <= 65535 ? number : null;
        return new(enabled, port);
    }
    public static NetworkPreferences ReadPreferences()
    {
        foreach (string root in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming"), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { string path = Path.Combine(root, "RenewedVision", "ProPresenter", "Preferences", "NetworkPreferences.proPref"); if (File.Exists(path)) return ParsePreferences(File.ReadAllText(path).TrimStart('\ufeff')); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return new(null, null);
    }
    public static async Task<bool> IsApi(int port, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(900) };
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/version", ct); response.EnsureSuccessStatusCode();
            var version = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            return version?["host_description"]?.ToString().StartsWith("ProPresenter ", StringComparison.OrdinalIgnoreCase) == true && version?["api_version"]?.ToString() == "v1";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (HttpRequestException) { return false; } catch (System.Text.Json.JsonException) { return false; } catch (InvalidOperationException) { return false; }
    }
    public static async Task<ApiStatus> Detect(Settings settings, CancellationToken ct)
    {
        var preferences = ReadPreferences();
        var ids = new HashSet<int>(); bool running = false;
        foreach (var process in Process.GetProcesses())
        {
            using (process) { try { string name = process.ProcessName; if (name == "ProPresenter") running = true; if (name == "ProPresenter" || name.StartsWith("ProPresenter Helper", StringComparison.OrdinalIgnoreCase)) ids.Add(process.Id); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }
        }
        int configured = settings.AutoDetectApi ? preferences.Port ?? settings.Port : settings.Port;
        var candidates = new List<int> { configured };
        if (settings.AutoDetectApi && running) candidates.AddRange(ListeningPorts(ids));
        var probes = candidates.Distinct().Take(32).Select(async port => (Port: port, Valid: await IsApi(port, ct))).ToArray();
        var results = await Task.WhenAll(probes); int? active = results.FirstOrDefault(result => result.Valid).Valid ? results.First(result => result.Valid).Port : null;
        string message = active != null ? $"API enabled and responding • Port {active}" : preferences.Enabled == false ? $"API disabled in ProPresenter’s Network settings • Port {configured}" : !running ? $"ProPresenter is not running • API setting: {(preferences.Enabled == true ? "enabled" : "unknown")} • Port {configured}" : preferences.Enabled == true ? $"API enabled in settings but not responding on port {configured}." : $"API status unknown • Cannot connect on port {configured}. Enable Network/API in ProPresenter’s settings.";
        return new(running, active != null ? true : preferences.Enabled, configured, active, message);
    }
    private static IEnumerable<int> ListeningPorts(HashSet<int> processIds)
    {
        var ports = new List<int>(); int size = 0; GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        if (size <= 0 || size > 16 * 1024 * 1024) return ports;
        IntPtr table = Marshal.AllocHGlobal(size);
        try
        {
            // TCP_TABLE_OWNER_PID_LISTENER (3), IPv4. Only inspect ProPresenter-owned listeners.
            if (GetExtendedTcpTable(table, ref size, false, 2, 3, 0) != 0) return ports;
            int count = Marshal.ReadInt32(table);
            for (int i = 0; i < count && 4 + (i + 1) * 24 <= size; i++)
            {
                IntPtr row = IntPtr.Add(table, 4 + i * 24);
                if (!processIds.Contains(Marshal.ReadInt32(row, 20))) continue;
                int encoded = Marshal.ReadInt32(row, 8); ports.Add(((encoded & 255) << 8) | ((encoded >> 8) & 255));
            }
        }
        catch (Exception) { } finally { Marshal.FreeHGlobal(table); }
        return ports;
    }
    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool ordered, uint family, int tableClass, uint reserved);
}
