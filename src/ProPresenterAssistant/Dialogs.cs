using System.Windows;
using System.Windows.Controls;
namespace ProPresenterAssistant;
public static class Dialogs
{
    public static bool EditSettings(Window owner, Settings settings)
    {
        var window = new Window { Owner = owner, Title = "Assistant settings", Width = 650, Height = 740, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner }; var panel = new StackPanel { Margin = new Thickness(28) }; window.Content = panel;
        void Label(string text) => panel.Children.Add(new TextBlock { Text = text });
        var automatic = new CheckBox { Content = "Automatically detect ProPresenter’s API port", IsChecked = settings.AutoDetectApi }; panel.Children.Add(automatic);
        Label("ProPresenter local API port (manual override when automatic detection is off)"); var port = new TextBox { Text = settings.Port.ToString(), IsEnabled = !settings.AutoDetectApi }; panel.Children.Add(port);
        var apiStatus = new TextBlock { Text = "Checking ProPresenter’s Network settings…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 14) }; panel.Children.Add(apiStatus);
        var stop = new CancellationTokenSource(); bool checking = false;
        async Task Detect()
        {
            if (checking) return; checking = true;
            try
            {
                int.TryParse(port.Text, out int value);
                var result = await ApiDiscovery.Detect(new Settings { AutoDetectApi = automatic.IsChecked == true, Port = value is > 0 and <= 65535 ? value : settings.Port }, stop.Token);
                apiStatus.Text = result.Message;
                if (automatic.IsChecked == true && (result.ActivePort ?? result.ConfiguredPort) is int found) port.Text = found.ToString();
            }
            catch (OperationCanceledException) { } catch (Exception ex) { apiStatus.Text = "API status could not be checked: " + ex.Message; }
            finally { checking = false; }
        }
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        window.Loaded += (_, _) => { _ = Detect(); timer.Start(); };
        timer.Tick += (_, _) => _ = Detect(); window.Closed += (_, _) => { timer.Stop(); stop.Cancel(); };
        automatic.Checked += (_, _) => { port.IsEnabled = false; _ = Detect(); }; automatic.Unchecked += (_, _) => { port.IsEnabled = true; _ = Detect(); };
        Label("ProPresenter executable"); var exe = new TextBox { Text = settings.Executable }; panel.Children.Add(exe);
        Label("Imported media folder"); var folder = new TextBox { Text = settings.MediaRoot }; panel.Children.Add(folder);
        Label("ProPresenter library folder (optional; leave blank for auto-detection)"); var library = new TextBox { Text = settings.LibraryFolder }; panel.Children.Add(library);
        var usb = new CheckBox { Content = "Watch for USB drives", IsChecked = settings.MonitorUsb }; panel.Children.Add(usb);
        var startup = new CheckBox { Content = "Start quietly when I sign into Windows", IsChecked = WindowsIntegration.IsStartupEnabled }; panel.Children.Add(startup);
        panel.Children.Add(new TextBlock { Text = "Network settings are read automatically and the API connection is verified. If disabled, enable Network access in ProPresenter. The assistant only connects to this computer.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 12) });
        var save = new Button { Content = "Save settings", HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(save);
        save.Click += (_, _) => { if (!int.TryParse(port.Text, out int value) || value < 1 || value > 65535 || !System.IO.File.Exists(exe.Text) || !System.IO.Path.IsPathFullyQualified(folder.Text) || (!string.IsNullOrWhiteSpace(library.Text) && (!System.IO.Path.IsPathFullyQualified(library.Text) || !System.IO.Directory.Exists(library.Text)))) { MessageBox.Show(window, "Enter a valid port, existing executable and absolute media folder. An optional library folder must already exist."); return; } settings.AutoDetectApi = automatic.IsChecked == true; settings.Port = value; settings.Executable = exe.Text; settings.MediaRoot = folder.Text; settings.LibraryFolder = library.Text.Trim(); settings.MonitorUsb = usb.IsChecked == true; WindowsIntegration.SetStartup(startup.IsChecked == true); window.DialogResult = true; };
        return window.ShowDialog() == true;
    }
    public static ImportJob? ChooseJob(Window owner, List<ImportJob> jobs)
    {
        var window = new Window { Owner = owner, Title = "Resume a prepared import", Width = 600, Height = 380, WindowStartupLocation = WindowStartupLocation.CenterOwner }; var panel = new StackPanel { Margin = new Thickness(24) }; window.Content = panel;
        panel.Children.Add(new TextBlock { Text = "Choose an import to finish", FontSize = 22, Margin = new Thickness(0, 0, 0, 20) }); var list = new ComboBox { ItemsSource = jobs.Select(j => j.Name + " • " + j.Count + " slides • " + j.State).ToList(), SelectedIndex = 0 }; panel.Children.Add(list);
        panel.Children.Add(new TextBlock { Text = "Your copied and converted media will be reused. An existing playlist entry will not be added twice.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16) }); var button = new Button { Content = "Resume import" }; panel.Children.Add(button); button.Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true ? jobs[list.SelectedIndex] : null;
    }
}
