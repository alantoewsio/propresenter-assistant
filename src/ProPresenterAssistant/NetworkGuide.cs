using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ProPresenterAssistant;

public static class NetworkGuide
{
    public static Window Create(Window owner)
    {
        var window = new Window
        {
            Owner = owner, Title = "Enable the ProPresenter API", Width = 700, Height = 790,
            MinWidth = 520, MinHeight = 450, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(16, 19, 27)), Foreground = Brushes.White,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 14
        };
        var layout = new DockPanel { Margin = new Thickness(24) };
        window.Content = layout;
        var close = new Button { Content = "Done", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), IsCancel = true };
        close.Click += (_, _) => window.Close();
        DockPanel.SetDock(close, Dock.Bottom); layout.Children.Add(close);
        var content = new StackPanel();
        layout.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        content.Children.Add(new TextBlock { Text = "Connect to ProPresenter", FontSize = 26, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = "Follow these steps in ProPresenter. The assistant checks the connection automatically.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, Margin = new Thickness(0, 8, 0, 20) });
        Step("1", "Open Settings", "Click ProPresenter in the top-left menu, then choose Settings.", "settings-menu", 240);
        Step("2", "Choose Network", "Select Network in the Settings sidebar. In older versions, look for the Network tab in Preferences.", "network-tab", 202);
        Step("3", "Turn on Enable Network", "Click the Enable Network switch so it is on, as shown below. Leave the port as it is.", "enable-network", 480);
        content.Children.Add(new TextBlock { Text = "Return to the assistant. Your playlists will appear when the API connects. If automatic port detection is off, enter ProPresenter’s Network port in the assistant’s Settings.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, Margin = new Thickness(0, 4, 0, 12) });
        content.Children.Add(new TextBlock { Text = "Screenshots: ProPresenter for Windows 21.4.2. Other versions may look slightly different.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12 });
        return window;

        void Step(string number, string title, string instruction, string asset, double width)
        {
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock { Text = number + "   " + title, FontSize = 18, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = instruction, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, Margin = new Thickness(0, 6, 0, 12) });
            panel.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri($"pack://application:,,,/Assets/ApiGuide/{asset}.png")),
                MaxWidth = width, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = title, SnapsToDevicePixels = true
            });
            content.Children.Add(new Border { Child = panel, Background = new SolidColorBrush(Color.FromRgb(27, 33, 46)), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 8, 12) });
        }
    }

    public static void Show(Window owner) => Create(owner).ShowDialog();
}
