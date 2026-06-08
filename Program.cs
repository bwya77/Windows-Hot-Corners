using System.Threading;
using HotCorners.Settings;

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
        // ticked. Persist the user's choice via the same SettingsStore the tray uses, so the
        // app stays the single owner of the HKCU Run key (TrayContext mirrors the setting to
        // the registry on startup and on every settings change).
        if (args.Any(a => string.Equals(a, "--enable-startup", StringComparison.OrdinalIgnoreCase)))
        {
            using var store = new SettingsStore();
            var snapshot = store.Current.Clone();
            snapshot.LaunchAtLogin = true;
            store.Save(snapshot);
        }

        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new TrayContext());
    }
}

