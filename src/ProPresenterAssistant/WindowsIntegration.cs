using System.IO;
using System.Management;
using Microsoft.Win32;
namespace ProPresenterAssistant;
public static class WindowsIntegration
{
    public const string StartupName = "ProPresenterAssistant";
    public static bool IsStartupEnabled => Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")?.GetValue(StartupName) != null;
    public static void SetStartup(bool enabled) { using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); if (enabled) key.SetValue(StartupName, "\"" + Environment.ProcessPath + "\" --tray"); else key.DeleteValue(StartupName, false); }
    public static HashSet<string> UsbVolumes()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives()) { try { if (drive.DriveType == DriveType.Removable && drive.IsReady) roots.Add(drive.RootDirectory.FullName); } catch { } }
        try
        {
            using var disks = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive WHERE InterfaceType='USB' OR PNPDeviceID LIKE 'USB%'"); using var results = disks.Get();
            foreach (ManagementObject disk in results) { using (disk) using (var partitions = disk.GetRelated("Win32_DiskPartition")) foreach (ManagementObject partition in partitions) { using (partition) using (var volumes = partition.GetRelated("Win32_LogicalDisk")) foreach (ManagementObject volume in volumes) { using (volume) { string root = volume["DeviceID"] + "\\"; try { if (new DriveInfo(root).IsReady) roots.Add(root); } catch { } } } } }
        }
        catch { }
        return roots;
    }
}
