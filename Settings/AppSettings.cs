using System.Text.Json.Serialization;

namespace HotCorners.Settings;

/// <summary>
/// The persisted user-facing settings for Hot Corners. Lives at
/// %APPDATA%\HotCorners\settings.json and is shared by the tray app and the
/// WinUI settings app — both processes read it via <see cref="SettingsStore"/>,
/// which atomically writes the file and signals other processes to reload.
/// </summary>
public sealed class AppSettings
{
    public Dictionary<Corner, HotAction> Bindings { get; set; } = new()
    {
        [Corner.TopLeft] = HotAction.TaskView,
        [Corner.TopRight] = HotAction.None,
        [Corner.BottomLeft] = HotAction.None,
        [Corner.BottomRight] = HotAction.None,
    };

    public int DwellMs { get; set; } = 25;

    public bool SuppressInFullscreen { get; set; } = true;

    public int CooldownMs { get; set; } = 500;

    public bool LaunchAtLogin { get; set; } = false;

    /// <summary>
    /// When true, show a translucent "puddle" overlay in the active corner while the
    /// cursor is dwelling there, and play a soft ripple when the action fires.
    /// </summary>
    public bool ShowCornerOverlay { get; set; } = true;

    /// <summary>
    /// Legacy field. Kept so settings files written by v0.4.x still deserialize without
    /// losing the user's "primary only" choice. On first launch after upgrading, the tray
    /// migrates PrimaryOnly into the per-monitor <see cref="DisabledMonitors"/> set and
    /// resets this back to AllMonitors. New code should treat DisabledMonitors as the
    /// source of truth for which displays are armed.
    /// </summary>
    public MultiMonitorMode MultiMonitor { get; set; } = MultiMonitorMode.AllMonitors;

    /// <summary>
    /// Device names (e.g. <c>\\.\DISPLAY1</c>) of monitors where hot corners should NOT
    /// fire. Empty means every connected display is armed (the default). Set membership
    /// is the only enable/disable signal — primary vs. secondary doesn't matter.
    /// Stable enough across reconnects for the same monitor on the same port; if the
    /// user reshuffles cables the picker will just show the new arrangement and they
    /// re-toggle.
    /// </summary>
    public HashSet<string> DisabledMonitors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ensure every corner has an entry — defends against an older settings file
    /// that pre-dates a newly-added corner enum value.</summary>
    public void Normalize()
    {
        foreach (Corner c in Enum.GetValues<Corner>())
        {
            if (c == Corner.None) continue;
            Bindings.TryAdd(c, HotAction.None);
        }
        // A v0.4.x file deserializes DisabledMonitors as a default-constructed HashSet
        // (case-sensitive). Reproject through the case-insensitive comparer so device
        // names like "\\.\DISPLAY1" match regardless of how Windows happens to spell them.
        if (DisabledMonitors.Comparer != StringComparer.OrdinalIgnoreCase)
            DisabledMonitors = new HashSet<string>(DisabledMonitors, StringComparer.OrdinalIgnoreCase);
    }

    public AppSettings Clone()
    {
        var copy = new AppSettings
        {
            DwellMs = DwellMs,
            SuppressInFullscreen = SuppressInFullscreen,
            CooldownMs = CooldownMs,
            LaunchAtLogin = LaunchAtLogin,
            ShowCornerOverlay = ShowCornerOverlay,
            MultiMonitor = MultiMonitor,
            Bindings = new Dictionary<Corner, HotAction>(Bindings),
            DisabledMonitors = new HashSet<string>(DisabledMonitors, StringComparer.OrdinalIgnoreCase),
        };
        return copy;
    }
}
