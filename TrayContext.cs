using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using HotCorners.Overlay;
using HotCorners.Settings;
using HotCorners.UI;
using HotCorners.Updates;

namespace HotCorners;

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _poll;
    private readonly SettingsStore _store = new();
    private readonly UpdateService _updateService;
    private readonly OverlayManager _overlay = new();

    private AppSettings _settings;
    private DateTime _enteredAt = DateTime.MinValue;
    private DateTime _lastFiredAt = DateTime.MinValue;
    private Corner _currentCorner = Corner.None;
    private bool _firedForThisEntry;
    private bool _paused;
    private Process? _settingsProcess;
    private UpdatePromptForm? _updateForm;
    private ToolStripMenuItem? _pauseItem;
    private readonly SynchronizationContext? _uiContext;

    public TrayContext()
    {
        _uiContext = SynchronizationContext.Current;
        _settings = _store.Current;
        _overlay.Enabled = _settings.ShowCornerOverlay;

        // Reconcile the HKCU Run key on every startup so it always points at the current
        // install path. The tray app stays the single owner of that key — the settings UI
        // only writes AppSettings.LaunchAtLogin and we mirror it to the registry here.
        StartupRegistration.Reconcile(_settings.LaunchAtLogin);

        _tray = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Hot Corners",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => OpenSettings();

        _poll = new System.Windows.Forms.Timer { Interval = 15 };
        _poll.Tick += OnPoll;
        _poll.Start();

        _updateService = new UpdateService(_tray, ShowUpdatePrompt);
        _updateService.StartBackgroundChecks();

        // External edits (from the WinUI settings app or a manual JSON tweak) arrive on a
        // background thread; marshal back onto the UI thread so any tray text refresh stays
        // single-threaded.
        _store.Changed += OnSettingsChanged;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = TrayMenu.Create(
            getPaused: () => _paused,
            getUpdate: () =>
            {
                var info = _updateService.PendingUpdate;
                return info != null
                    ? (true, "v" + info.Latest.ToString(3))
                    : (false, (string?)null);
            },
            onUpdate: () =>
            {
                var info = _updateService.PendingUpdate;
                if (info != null) ShowUpdatePrompt(info);
            },
            onSettings: OpenSettings,
            onTogglePause: () =>
            {
                _paused = !_paused;
                _tray.Text = _paused ? "Hot Corners (paused)" : "Hot Corners";
                if (_paused) _overlay.Cancel();
            },
            onCheckUpdates: async () => await _updateService.CheckAsync(showIfUpToDate: true),
            onAbout: ShowAbout,
            onQuit: () => ExitThread(),
            out _pauseItem);

        return menu;
    }

    private static void ShowAbout()
    {
        var v = UpdateChecker.CurrentVersion.ToString(3);
        MessageBox.Show(
            $"Hot Corners for Windows\nVersion {v}\n\nMove your cursor into a screen corner to trigger an action.\n\nhttps://github.com/bwya77/Windows-Hot-Corners",
            "About Hot Corners",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void OnSettingsChanged(AppSettings updated)
    {
        // Always update the cached snapshot — even before we marshal — so the poll loop
        // (which runs on the UI thread) sees the latest values.
        _settings = updated;

        void apply()
        {
            // Mirror the LaunchAtLogin toggle to the HKCU Run key. The settings UI doesn't
            // touch the registry directly; only the tray app does.
            StartupRegistration.Reconcile(updated.LaunchAtLogin);

            // Live-toggle the puddle overlay too.
            _overlay.Enabled = updated.ShowCornerOverlay;
            if (!updated.ShowCornerOverlay) _overlay.Cancel();
        }

        if (_uiContext != null) _uiContext.Post(_ => apply(), null);
        else apply();
    }

    /// <summary>
    /// Open the standalone WinUI 3 settings window. If it's already running, focus its
    /// existing process; otherwise launch HotCorners.Settings.exe (which sits in the
    /// "Settings" subfolder next to the tray exe per the installer layout).
    /// </summary>
    private void OpenSettings()
    {
        if (_settingsProcess is { HasExited: false })
        {
            try
            {
                if (_settingsProcess.MainWindowHandle != IntPtr.Zero)
                    SetForegroundWindow(_settingsProcess.MainWindowHandle);
                return;
            }
            catch { /* fall through and try to start a new one */ }
        }

        var path = ResolveSettingsExePath();
        if (path == null || !File.Exists(path))
        {
            MessageBox.Show(
                "Couldn't find HotCorners.Settings.exe. Try reinstalling Hot Corners.",
                "Hot Corners",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        try
        {
            _settingsProcess = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Couldn't launch the settings window:\n\n{ex.Message}",
                "Hot Corners",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Look up the settings exe path. Installer drops it at <c>&lt;app&gt;\Settings\HotCorners.Settings.exe</c>.
    /// During <c>dotnet run</c> dev builds, fall back to the sibling project's published or
    /// build output so devs can iterate without re-publishing.
    /// </summary>
    private static string? ResolveSettingsExePath()
    {
        var baseDir = AppContext.BaseDirectory;

        // 1. Installer layout: alongside in a Settings subfolder.
        var installed = Path.Combine(baseDir, "Settings", "HotCorners.Settings.exe");
        if (File.Exists(installed)) return installed;

        // 2. Same folder (single-folder publish).
        var sibling = Path.Combine(baseDir, "HotCorners.Settings.exe");
        if (File.Exists(sibling)) return sibling;

        // 3. Dev layout: ..\..\..\..\HotCorners.Settings\bin\<Configuration>\net8.0-windows10.0.19041.0\win-x64\HotCorners.Settings.exe
        try
        {
            var repoRoot = baseDir;
            for (int i = 0; i < 6 && repoRoot != null; i++)
            {
                var candidate = Path.Combine(repoRoot, "HotCorners.Settings", "bin");
                if (Directory.Exists(candidate))
                {
                    var exe = Directory.GetFiles(candidate, "HotCorners.Settings.exe", SearchOption.AllDirectories)
                                       .OrderByDescending(File.GetLastWriteTimeUtc)
                                       .FirstOrDefault();
                    if (exe != null) return exe;
                }
                repoRoot = Path.GetDirectoryName(repoRoot.TrimEnd(Path.DirectorySeparatorChar));
            }
        }
        catch { /* dev fallback only */ }

        return null;
    }

    private void ShowUpdatePrompt(UpdateChecker.UpdateInfo info)
    {
        if (_updateForm is { IsDisposed: false })
        {
            _updateForm.Activate();
            return;
        }
        _updateForm = new UpdatePromptForm(info);
        _updateForm.FormClosed += (_, _) => _updateForm = null;
        _updateForm.Show();
    }

    private void OnPoll(object? sender, EventArgs e)
    {
        if (_paused) { _overlay.Cancel(); return; }
        if (!GetCursorPos(out var pt)) return;

        var (corner, screen) = DetectCorner(pt, _settings.MultiMonitor);

        if (corner != _currentCorner)
        {
            _currentCorner = corner;
            _enteredAt = DateTime.UtcNow;
            _firedForThisEntry = false;

            if (corner == Corner.None || screen == null)
            {
                _overlay.Cancel();
            }
            else
            {
                var action = _settings.Bindings.TryGetValue(corner, out var aNew) ? aNew : HotAction.None;
                // Only start the puddle for corners that actually do something.
                if (action == HotAction.None || (_settings.SuppressInFullscreen && Fullscreen.ForegroundIsFullscreen()))
                    _overlay.Cancel();
                else
                    _overlay.BeginDwell(corner, screen.Bounds, _settings.DwellMs);
            }
            return;
        }

        if (corner == Corner.None || _firedForThisEntry) return;

        if ((DateTime.UtcNow - _enteredAt).TotalMilliseconds < _settings.DwellMs) return;
        if ((DateTime.UtcNow - _lastFiredAt).TotalMilliseconds < _settings.CooldownMs) return;

        var action2 = _settings.Bindings.TryGetValue(corner, out var a) ? a : HotAction.None;
        if (action2 == HotAction.None) return;

        if (_settings.SuppressInFullscreen && Fullscreen.ForegroundIsFullscreen())
        {
            _overlay.Cancel();
            return;
        }

        _firedForThisEntry = true;
        _lastFiredAt = DateTime.UtcNow;
        _overlay.PlayRipple();
        ActionRunner.Run(action2);
    }

    // A corner only counts when the cursor is bumped on BOTH axes — i.e. no neighboring
    // monitor in the relevant direction. This makes "internal" corners between monitors
    // (where the cursor can keep moving) inert, which matches macOS hot-corner behavior.
    // Returns the matching Screen so the overlay knows which monitor to anchor to.
    // When mode == PrimaryOnly, only the Windows primary display participates.
    private static (Corner, Screen?) DetectCorner(POINT pt, MultiMonitorMode mode)
    {
        const int tol = 2;
        var all = Screen.AllScreens;
        var armed = mode == MultiMonitorMode.PrimaryOnly
            ? new[] { Screen.PrimaryScreen! }
            : all;

        foreach (var s in armed)
        {
            var b = s.Bounds;
            var atLeft = pt.X <= b.Left + tol;
            var atRight = pt.X >= b.Right - 1 - tol;
            var atTop = pt.Y <= b.Top + tol;
            var atBottom = pt.Y >= b.Bottom - 1 - tol;

            if (!((atLeft || atRight) && (atTop || atBottom))) continue;

            var leftBlocked = atLeft && HasNeighborHorizontally(armed, s, pt.Y, leftSide: true);
            var rightBlocked = atRight && HasNeighborHorizontally(armed, s, pt.Y, leftSide: false);
            var topBlocked = atTop && HasNeighborVertically(armed, s, pt.X, topSide: true);
            var bottomBlocked = atBottom && HasNeighborVertically(armed, s, pt.X, topSide: false);

            if (atTop && atLeft && !topBlocked && !leftBlocked) return (Corner.TopLeft, s);
            if (atTop && atRight && !topBlocked && !rightBlocked) return (Corner.TopRight, s);
            if (atBottom && atLeft && !bottomBlocked && !leftBlocked) return (Corner.BottomLeft, s);
            if (atBottom && atRight && !bottomBlocked && !rightBlocked) return (Corner.BottomRight, s);
        }
        return (Corner.None, null);
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

    /// <summary>Load the embedded tray icon (multi-size .ico) and let GDI+ pick the
    /// best fit for the system's small-icon dimensions. Falls back to the generic
    /// SystemIcons.Application if the embedded resource ever goes missing.</summary>
    private static Icon LoadTrayIcon()
    {
        try
        {
            using var stream = typeof(TrayContext).Assembly
                .GetManifestResourceStream("HotCorners.icon-tray.ico");
            if (stream != null)
                return new Icon(stream, SystemInformation.SmallIconSize);
        }
        catch { /* fall through to default */ }
        return SystemIcons.Application;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poll.Stop();
            _poll.Dispose();
            _updateService.Dispose();
            _store.Changed -= OnSettingsChanged;
            _store.Dispose();
            _overlay.Dispose();
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
