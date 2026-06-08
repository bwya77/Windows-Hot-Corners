using Microsoft.Win32;

namespace HotCorners;

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HotCorners";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (key is null) return;

        if (enabled)
        {
            var exe = Environment.ProcessPath ?? AppContext.BaseDirectory;
            key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Reconciles the HKCU Run key with the app-owned LaunchAtLogin setting. Called on every
    /// app start so the value always points at the current install path (handy after the
    /// installer moved the binary from %LocalAppData% to Program Files, for example).
    /// </summary>
    public static void Reconcile(bool wanted)
    {
        try
        {
            SetEnabled(wanted);
        }
        catch
        {
            // Best-effort — the user can still toggle from Settings.
        }
    }
}
