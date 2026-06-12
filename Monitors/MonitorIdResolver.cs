using System.Runtime.InteropServices;

namespace HotCorners.Monitors;

/// <summary>
/// Maps a Windows adapter device name (the <c>\\.\DISPLAY1</c>-style identifier
/// returned by <c>EnumDisplayDevices</c> at the adapter level, also matching
/// <c>System.Windows.Forms.Screen.DeviceName</c>) to a stable per-monitor hardware
/// identifier sourced from <c>EnumDisplayDevices</c> at the monitor level with the
/// <c>EDD_GET_DEVICE_INTERFACE_NAME</c> flag. The returned ID looks like
/// <c>\\?\DISPLAY#DELA0DC#7&amp;1c8e8de7&amp;0&amp;UID8453#{e6f07b5f-…}</c> — the
/// manufacturer+model portion (<c>DELA0DC</c>) is fixed for that physical panel and
/// the rest is bus-instance-stable per port, which matches Windows' own
/// "remember per-monitor settings" behavior.
///
/// Both the tray (filtering screens at corner-detect time) and the settings UI
/// (painting the monitor picker) call this so they agree on what counts as "the
/// same monitor". <c>AppSettings.DisabledMonitors</c> stores hardware IDs only.
/// </summary>
internal static class MonitorIdResolver
{
    private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;
    private static readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _gate = new();

    /// <summary>True if the given key looks like a Windows session-only adapter name
    /// (e.g. <c>\\.\DISPLAY1</c>). Used by the v0.5.0 → v0.5.1 migration to find
    /// disabled-monitor entries that need to be upgraded to stable hardware IDs.</summary>
    public static bool IsLegacyAdapterKey(string key) =>
        !string.IsNullOrEmpty(key) && key.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase);

    /// <summary>True if the given key still carries the un-normalized device interface
    /// form (e.g. <c>\\?\DISPLAY#DELA0DC#7&amp;1c8e8de7&amp;0&amp;UID8453#{guid}</c>) — the
    /// middle "instance" segment is bus-path info that can shift across reboots, dock
    /// connect/disconnect, or USB-C topology changes, which previously broke per-monitor
    /// settings for users whose saved keys included it. The v0.5.5 migration normalizes
    /// these to a stable EDID-based form via <see cref="NormalizeHardwareId"/>.</summary>
    public static bool IsUnnormalizedDeviceId(string key) =>
        !string.IsNullOrEmpty(key) && key.StartsWith(@"\\?\DISPLAY#", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reduce a raw <c>EnumDisplayDevices</c> device interface name to its stable
    /// EDID-based portion. The full form looks like:
    /// <c>\\?\DISPLAY#DELA0DC#7&amp;1c8e8de7&amp;0&amp;UID8453#{guid}</c> — the middle
    /// <c>7&amp;1c8e8de7&amp;0</c> segment is bus-instance info that can change across
    /// reboots and dock topology shifts, so we drop it and keep just the EDID
    /// manufacturer+model code plus the <c>UID####</c> token (which identifies which
    /// physical port the monitor is attached to). Result looks like <c>DELA0DC#UID8453</c>
    /// or just <c>DELA0DC</c> when no UID is present.
    ///
    /// Inputs that don't match the expected form are returned unchanged so legacy
    /// adapter names like <c>\\.\DISPLAY1</c> still flow through earlier migrations.
    /// </summary>
    public static string NormalizeHardwareId(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        if (!raw.StartsWith(@"\\?\DISPLAY#", StringComparison.OrdinalIgnoreCase)) return raw;
        var parts = raw.Split('#');
        if (parts.Length < 2) return raw;
        var edid = parts[1];
        if (string.IsNullOrEmpty(edid)) return raw;
        string? uid = null;
        if (parts.Length >= 3)
        {
            foreach (var token in parts[2].Split('&'))
            {
                if (token.StartsWith("UID", StringComparison.OrdinalIgnoreCase))
                {
                    uid = token;
                    break;
                }
            }
        }
        return uid != null ? $"{edid}#{uid}" : edid;
    }

    /// <summary>Resolve <paramref name="adapterDeviceName"/> (e.g. <c>\\.\DISPLAY1</c>)
    /// to its stable, normalized hardware identifier. Cached because the tray re-resolves
    /// every 15 ms poll tick — call <see cref="InvalidateCache"/> when displays change.
    /// Returns the adapter name unchanged if the resolution fails so the caller
    /// always has a usable key.</summary>
    public static string Resolve(string adapterDeviceName)
    {
        if (string.IsNullOrEmpty(adapterDeviceName)) return adapterDeviceName;
        lock (_gate)
        {
            if (_cache.TryGetValue(adapterDeviceName, out var cached)) return cached;
        }
        var resolved = NormalizeHardwareId(ResolveUncached(adapterDeviceName));
        lock (_gate) _cache[adapterDeviceName] = resolved;
        return resolved;
    }

    /// <summary>Drop the cache so the next <see cref="Resolve"/> calls hit Win32
    /// again. Wire to <c>SystemEvents.DisplaySettingsChanged</c> (tray) or to a
    /// <c>WM_DISPLAYCHANGE</c> listener (UI) so a dock/undock invalidates stale
    /// adapter-name → hardware-ID mappings.</summary>
    public static void InvalidateCache()
    {
        lock (_gate) _cache.Clear();
    }

    private static string ResolveUncached(string adapterDeviceName)
    {
        try
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevicesW(adapterDeviceName, 0, ref dd, EDD_GET_DEVICE_INTERFACE_NAME))
            {
                var id = dd.DeviceID?.Trim() ?? "";
                if (!string.IsNullOrEmpty(id)) return id;
            }
        }
        catch
        {
            // Best-effort: fall through to the adapter name.
        }
        return adapterDeviceName;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);
}
