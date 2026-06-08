using System.Threading;

namespace HotCorners;

internal static class Program
{
    private static Mutex? _singleInstance;

    [STAThread]
    private static void Main()
    {
        _singleInstance = new Mutex(true, "HotCorners.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new TrayContext());
    }
}
