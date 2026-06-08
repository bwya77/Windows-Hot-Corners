namespace HotCorners;

public static class ActionLabels
{
    public static string Pretty(HotAction a) => a switch
    {
        HotAction.None => "—",
        HotAction.TaskView => "Task View",
        HotAction.ShowDesktop => "Show Desktop",
        HotAction.ActionCenter => "Quick Settings",
        HotAction.NotificationCenter => "Notification Center",
        HotAction.StartMenu => "Start Menu",
        HotAction.Search => "Search",
        HotAction.Widgets => "Widgets",
        HotAction.LockScreen => "Lock Screen",
        HotAction.VirtualDesktopLeft => "Previous Desktop",
        HotAction.VirtualDesktopRight => "Next Desktop",
        HotAction.WindowSnapLeft => "Snap Window Left",
        HotAction.WindowSnapRight => "Snap Window Right",
        HotAction.MinimizeAll => "Minimize All",
        HotAction.SleepDisplays => "Put Display to Sleep",
        _ => a.ToString(),
    };
}
