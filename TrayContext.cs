using System.Runtime.InteropServices;

namespace HotCorners;

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _poll;
    private readonly Settings _settings = Settings.Load();

    private DateTime _enteredAt = DateTime.MinValue;
    private DateTime _lastFiredAt = DateTime.MinValue;
    private Corner _currentCorner = Corner.None;
    private bool _firedForThisEntry;
    private bool _paused;
    private SettingsForm? _settingsForm;
    private ToolStripMenuItem? _pauseItem;

    public TrayContext()
    {
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Hot Corners",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => OpenSettings();

        _poll = new System.Windows.Forms.Timer { Interval = 15 };
        _poll.Tick += OnPoll;
        _poll.Start();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var settings = new ToolStripMenuItem("Hot Corners Settings\u2026");
        settings.Click += (_, _) => OpenSettings();
        menu.Items.Add(settings);

        _pauseItem = new ToolStripMenuItem("Pause") { CheckOnClick = true };
        _pauseItem.CheckedChanged += (_, _) =>
        {
            _paused = _pauseItem!.Checked;
            _tray.Text = _paused ? "Hot Corners (paused)" : "Hot Corners";
        };
        menu.Items.Add(_pauseItem);

        menu.Items.Add(new ToolStripSeparator());

        var about = new ToolStripMenuItem("About Hot Corners\u2026");
        about.Click += (_, _) =>
        {
            var v = typeof(TrayContext).Assembly.GetName().Version?.ToString(3) ?? "?";
            MessageBox.Show(
                $"Hot Corners for Windows\nVersion {v}\n\nMove your cursor into a screen corner to trigger an action.\n\nhttps://github.com/bwya77/Windows-Hot-Corners",
                "About Hot Corners",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        };
        menu.Items.Add(about);

        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        return menu;
    }

    private void OpenSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_settings);
        _settingsForm.FormClosed += (_, _) => _settingsForm = null;
        _settingsForm.Show();
    }

    private void OnPoll(object? sender, EventArgs e)
    {
        if (_paused) return;
        if (!GetCursorPos(out var pt)) return;

        var corner = DetectCorner(pt);

        if (corner != _currentCorner)
        {
            _currentCorner = corner;
            _enteredAt = DateTime.UtcNow;
            _firedForThisEntry = false;
            return;
        }

        if (corner == Corner.None || _firedForThisEntry) return;

        if ((DateTime.UtcNow - _enteredAt).TotalMilliseconds < _settings.DwellMs) return;
        if ((DateTime.UtcNow - _lastFiredAt).TotalMilliseconds < _settings.CooldownMs) return;

        var action = _settings.Bindings.TryGetValue(corner, out var a) ? a : HotAction.None;
        if (action == HotAction.None) return;

        if (_settings.SuppressInFullscreen && Fullscreen.ForegroundIsFullscreen()) return;

        _firedForThisEntry = true;
        _lastFiredAt = DateTime.UtcNow;
        ActionRunner.Run(action);
    }

    // A corner only counts when the cursor is bumped on BOTH axes — i.e. no neighboring
    // monitor in the relevant direction. This makes "internal" corners between monitors
    // (where the cursor can keep moving) inert, which matches macOS hot-corner behavior.
    private static Corner DetectCorner(POINT pt)
    {
        const int tol = 2;
        var all = Screen.AllScreens;

        foreach (var s in all)
        {
            var b = s.Bounds;
            var atLeft = pt.X <= b.Left + tol;
            var atRight = pt.X >= b.Right - 1 - tol;
            var atTop = pt.Y <= b.Top + tol;
            var atBottom = pt.Y >= b.Bottom - 1 - tol;

            if (!((atLeft || atRight) && (atTop || atBottom))) continue;

            var leftBlocked = atLeft && HasNeighborHorizontally(all, s, pt.Y, leftSide: true);
            var rightBlocked = atRight && HasNeighborHorizontally(all, s, pt.Y, leftSide: false);
            var topBlocked = atTop && HasNeighborVertically(all, s, pt.X, topSide: true);
            var bottomBlocked = atBottom && HasNeighborVertically(all, s, pt.X, topSide: false);

            if (atTop && atLeft && !topBlocked && !leftBlocked) return Corner.TopLeft;
            if (atTop && atRight && !topBlocked && !rightBlocked) return Corner.TopRight;
            if (atBottom && atLeft && !bottomBlocked && !leftBlocked) return Corner.BottomLeft;
            if (atBottom && atRight && !bottomBlocked && !rightBlocked) return Corner.BottomRight;
        }
        return Corner.None;
    }

    private static bool HasNeighborHorizontally(Screen[] all, Screen self, int y, bool leftSide)
    {
        var b = self.Bounds;
        foreach (var o in all)
        {
            if (o == self) continue;
            var ob = o.Bounds;
            if (y < ob.Top || y >= ob.Bottom) continue;
            if (leftSide && ob.Right > b.Left && ob.Left < b.Left) return true;
            if (!leftSide && ob.Left < b.Right && ob.Right > b.Right) return true;
        }
        return false;
    }

    private static bool HasNeighborVertically(Screen[] all, Screen self, int x, bool topSide)
    {
        var b = self.Bounds;
        foreach (var o in all)
        {
            if (o == self) continue;
            var ob = o.Bounds;
            if (x < ob.Left || x >= ob.Right) continue;
            if (topSide && ob.Bottom > b.Top && ob.Top < b.Top) return true;
            if (!topSide && ob.Top < b.Bottom && ob.Bottom > b.Bottom) return true;
        }
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poll.Stop();
            _poll.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
