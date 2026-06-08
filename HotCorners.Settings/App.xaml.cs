using Microsoft.UI.Xaml;

namespace HotCorners.SettingsApp;

/// <summary>
/// Entry point for the standalone WinUI 3 settings app. The main Hot Corners tray
/// app launches this as a separate process (HotCorners.Settings.exe); the two stay
/// in sync through the shared settings.json file (see SettingsStore's file watcher
/// and named cross-process signal).
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
