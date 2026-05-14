using System.Windows;

namespace NakTaWallpaper;

public partial class OverlayWidgetWindow : Window
{
    private readonly MainWindow _wallpaperWindow;

    public OverlayWidgetWindow(MainWindow wallpaperWindow)
    {
        InitializeComponent();
        _wallpaperWindow = wallpaperWindow;
    }

    public void UpdateMemoryStatus(string status)
    {
        MemoryTextBlock.Text = status;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
    }
}
