using System.Threading;

namespace HotCorners;

internal static class Program
{
    private static Mutex? _singleInstance;

    [STAThread]
    private static void Main(string[] args)
    {
        _singleInstance = new Mutex(true, "HotCorners.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            return;
        }

        // The Inno installer passes --enable-startup when the "Start with Windows" task was
        // ticked. Persist the user's choice so the app stays the single owner of the HKCU Run
        // key; TrayContext reconciles the registry value on startup based on this setting.
        if (args.Any(a => string.Equals(a, "--enable-startup", StringComparison.OrdinalIgnoreCase)))
        {
            var s = Settings.Load();
            s.LaunchAtLogin = true;
            s.Save();
        }

        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new TrayContext());
    }
}
