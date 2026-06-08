namespace HotCorners;

public enum Corner
{
    None,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

public enum HotAction
{
    None,
    TaskView,            // Win+Tab
    ShowDesktop,         // Win+D
    ActionCenter,        // Win+A
    NotificationCenter,  // Win+N
    StartMenu,           // Win
    Search,              // Win+S
    Widgets,             // Win+W
    LockScreen,          // Win+L
    VirtualDesktopLeft,  // Ctrl+Win+Left
    VirtualDesktopRight, // Ctrl+Win+Right
    WindowSnapLeft,      // Win+Left
    WindowSnapRight,     // Win+Right
    MinimizeAll,         // Win+M
    SleepDisplays,       // Monitor off
}

public enum MultiMonitorMode
{
    /// <summary>Every connected display has its own outer corners armed (default).</summary>
    AllMonitors,
    /// <summary>Only the Windows primary display fires hot corners; the others are inert.</summary>
    PrimaryOnly,
}
