using System.Runtime.InteropServices;

namespace HotCorners;

internal static class Fullscreen
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    // Shell hosts that legitimately cover the screen but shouldn't suppress hot corners
    // (Task View lives in explorer.exe, Start lives in StartMenuExperienceHost, etc.).
    // Exempting these is what lets a corner re-fire to TOGGLE Task View off.
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "shellexperiencehost",
        "startmenuexperiencehost",
        "searchhost",
        "searchui",
        "textinputhost",
        "dwm",
        "lockapp",
    };

    public static bool ForegroundIsFullscreen()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        // Skip the desktop and the shell — they cover the screen but aren't true fullscreen apps.
        var sb = new System.Text.StringBuilder(256);
        if (GetClassName(hwnd, sb, sb.Capacity) > 0)
        {
            var cls = sb.ToString();
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        }

        // Exempt windows owned by shell-host processes (Task View, Start, Search, etc.) so the
        // hot corner can re-fire and toggle them off.
        if (GetWindowThreadProcessId(hwnd, out var pid) != 0 && pid != 0)
        {
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                if (ShellProcesses.Contains(proc.ProcessName)) return false;
            }
            catch
            {
                // Process exited between calls — fall through.
            }
        }

        if (!GetWindowRect(hwnd, out var win)) return false;

        var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero) return false;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;

        return win.Left <= mi.Monitor.Left
            && win.Top <= mi.Monitor.Top
            && win.Right >= mi.Monitor.Right
            && win.Bottom >= mi.Monitor.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }
}
