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

    public int DwellMs { get; set; } = 150;

    public bool SuppressInFullscreen { get; set; } = true;

    public int CooldownMs { get; set; } = 500;

    public bool LaunchAtLogin { get; set; } = false;

    /// <summary>Ensure every corner has an entry — defends against an older settings file
    /// that pre-dates a newly-added corner enum value.</summary>
    public void Normalize()
    {
        foreach (Corner c in Enum.GetValues<Corner>())
        {
            if (c == Corner.None) continue;
            Bindings.TryAdd(c, HotAction.None);
        }
    }

    public AppSettings Clone()
    {
        var copy = new AppSettings
        {
            DwellMs = DwellMs,
            SuppressInFullscreen = SuppressInFullscreen,
            CooldownMs = CooldownMs,
            LaunchAtLogin = LaunchAtLogin,
            Bindings = new Dictionary<Corner, HotAction>(Bindings),
        };
        return copy;
    }
}
