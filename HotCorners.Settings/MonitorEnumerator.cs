using System.Runtime.InteropServices;

namespace HotCorners.SettingsApp;

/// <summary>
/// Pure-Win32 enumeration of physical monitors for the settings UI. The tray app uses
/// <c>System.Windows.Forms.Screen</c> for the same data, but this WinUI 3 project has
/// no WinForms reference, so we go straight to <c>EnumDisplayMonitors</c> +
/// <c>GetMonitorInfoW</c> + <c>EnumDisplayDevicesW</c>. The <see cref="MonitorInfo.DeviceName"/>
/// here matches <c>Screen.DeviceName</c> verbatim ("\\.\DISPLAY1"), so the disabled-monitor
/// set written by this UI is the same key set the tray reads at corner-detection time.
/// </summary>
internal static class MonitorEnumerator
{
    public static IReadOnlyList<MonitorInfo> EnumerateAll()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoW(hMon, ref mi))
            {
                var name = mi.szDevice ?? "";
                list.Add(new MonitorInfo(
                    DeviceName: name,
                    FriendlyName: ResolveFriendlyName(name),
                    Left: mi.rcMonitor.Left,
                    Top: mi.rcMonitor.Top,
                    Right: mi.rcMonitor.Right,
                    Bottom: mi.rcMonitor.Bottom,
                    IsPrimary: (mi.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        }, IntPtr.Zero);

        // Number monitors the way Windows Settings > Display does: primary is #1, then
        // by position (top-to-bottom, left-to-right). This matches user expectation when
        // they look at the picker next to the system's display arrangement.
        var ordered = list
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.Top)
            .ThenBy(m => m.Left)
            .ToList();
        for (int i = 0; i < ordered.Count; i++) ordered[i] = ordered[i] with { Index = i + 1 };
        return ordered;
    }

    /// <summary>Look up the human-readable model string for a display adapter
    /// (e.g. "DELL U2720Q"). Falls back to empty so the UI can show "Display N" only.</summary>
    private static string ResolveFriendlyName(string adapterDeviceName)
    {
        if (string.IsNullOrEmpty(adapterDeviceName)) return "";
        try
        {
            // EnumDisplayDevices with a non-null lpDevice enumerates monitors attached
            // to that adapter. Index 0 is the active monitor on that output.
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevicesW(adapterDeviceName, 0, ref dd, 0))
            {
                var s = dd.DeviceString?.Trim() ?? "";
                if (!string.IsNullOrEmpty(s) && !string.Equals(s, "Generic PnP Monitor", StringComparison.OrdinalIgnoreCase))
                    return s;
            }
        }
        catch { /* best effort */ }
        return "";
    }

    // ---- P/Invoke ----------------------------------------------------------

    private const uint MONITORINFOF_PRIMARY = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);
}

internal sealed record MonitorInfo(
    string DeviceName,
    string FriendlyName,
    int Left,
    int Top,
    int Right,
    int Bottom,
    bool IsPrimary,
    int Index = 0)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}
