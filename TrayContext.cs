using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using HotCorners.Monitors;
using HotCorners.Overlay;
using HotCorners.Settings;
using HotCorners.UI;
using HotCorners.Updates;

namespace HotCorners;

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _poll;
    private readonly System.Windows.Forms.Timer _trayWatchdog;
    private readonly TaskbarRecreatedListener _taskbarListener;
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
    private bool _updateInFlight;
    private ToolStripMenuItem? _pauseItem;
    private readonly SynchronizationContext? _uiContext;

    public TrayContext()
    {
        _uiContext = SynchronizationContext.Current;
        _settings = _store.Current;
        _overlay.Enabled = _settings.ShowCornerOverlay;

        // One-time migration: v0.4.x exposed a "PrimaryOnly" radio. v0.5+ replaced it
        // with a per-monitor picker keyed by device name. Translate the old choice into
        // the new model on first launch after upgrading, so the user's "only primary"
        // preference survives without them having to redo it.
        MigrateLegacyMonitorMode();

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

        // Bulletproof tray icon visibility. Two layers:
        //   1. TaskbarRecreatedListener — re-adds the icon when Explorer restarts (Explorer
        //      broadcasts the registered "TaskbarCreated" message). WinForms NotifyIcon
        //      already handles this internally in most cases but has been observed to miss
        //      it under heavy boot or session-switch races.
        //   2. _trayWatchdog — a 60s safety net that re-issues NIM_ADD even if we missed
        //      the broadcast (e.g. icon was created before Explorer was fully ready).
        // Both work by toggling Visible, which forces Shell_NotifyIcon(NIM_ADD) and is a
        // no-op visually when the icon is already showing.
        _taskbarListener = new TaskbarRecreatedListener(EnsureTrayVisible);
        _trayWatchdog = new System.Windows.Forms.Timer { Interval = 60_000 };
        _trayWatchdog.Tick += (_, _) => EnsureTrayVisible();
        _trayWatchdog.Start();

        _updateService = new UpdateService(_tray, info => _ = ApplyPendingUpdateAsync(info));
        _updateService.StartBackgroundChecks();

        // Drop the adapter-name → hardware-ID cache whenever the display arrangement
        // changes (dock, undock, monitor connect/disconnect, layout reshuffle). Also
        // re-run migration in case a legacy \\.\DISPLAYn key that we couldn't resolve
        // at startup now matches a freshly-connected display.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // External edits (from the WinUI settings app or a manual JSON tweak) arrive on a
        // background thread; marshal back onto the UI thread so any tray text refresh stays
        // single-threaded.
        _store.Changed += OnSettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        MonitorIdResolver.InvalidateCache();
        if (_uiContext != null) _uiContext.Post(_ => MigrateLegacyMonitorMode(), null);
        else MigrateLegacyMonitorMode();
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
                if (info != null) _ = ApplyPendingUpdateAsync(info);
            },
            onSettings: () => OpenSettings(),
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

    private void MigrateLegacyMonitorMode()
    {
        var changed = false;
        var migrated = _settings.Clone();

        // 1. v0.4.x PrimaryOnly radio → per-monitor disabled set, keyed by stable
        //    hardware ID (not the session-only \\.\DISPLAYn name).
        if (migrated.MultiMonitor == MultiMonitorMode.PrimaryOnly)
        {
            foreach (var s in Screen.AllScreens)
            {
                if (!s.Primary) migrated.DisabledMonitors.Add(MonitorIdResolver.Resolve(s.DeviceName));
            }
            migrated.MultiMonitor = MultiMonitorMode.AllMonitors;
            changed = true;
        }

        // 2. v0.5.0 stored \\.\DISPLAYn keys directly. Those aren't stable across
        //    reconnects / docking, so upgrade any such entries to their current
        //    hardware ID. Entries that don't currently resolve are dropped — the
        //    device isn't here, and the legacy key is meaningless without it.
        var legacy = migrated.DisabledMonitors.Where(MonitorIdResolver.IsLegacyAdapterKey).ToList();
        if (legacy.Count > 0)
        {
            foreach (var key in legacy)
            {
                migrated.DisabledMonitors.Remove(key);
                var match = Screen.AllScreens.FirstOrDefault(s =>
                    string.Equals(s.DeviceName, key, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    var hwid = MonitorIdResolver.Resolve(match.DeviceName);
                    if (!string.IsNullOrEmpty(hwid)) migrated.DisabledMonitors.Add(hwid);
                }
            }
            changed = true;
        }

        // 3. v0.5.1 stored whole-monitor entries in DisabledMonitors. v0.5.2 moved to
        //    per-corner control. Expand each whole-monitor entry into all four corner
        //    keys so the user's "this display off" preference becomes "all four corners
        //    of this display off" with no change in observable behavior. Empty the
        //    legacy set after expansion so it stays authoritative-source-of-truth-free.
        if (migrated.DisabledMonitors.Count > 0)
        {
            foreach (var hwid in migrated.DisabledMonitors)
            {
                foreach (Corner c in Enum.GetValues<Corner>())
                {
                    if (c == Corner.None) continue;
                    migrated.DisabledMonitorCorners.Add(CornerKey(hwid, c));
                }
            }
            migrated.DisabledMonitors.Clear();
            changed = true;
        }

        if (changed)
        {
            _store.Save(migrated);
            _settings = migrated;
        }
    }

    /// <summary>Compose a single dictionary key for a (display, corner) pair.</summary>
    internal static string CornerKey(string hardwareId, Corner corner) => hardwareId + "|" + corner;

    private void ShowAbout() => OpenSettings("about");

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
    /// "Settings" subfolder next to the tray exe per the installer layout). An optional
    /// pane tag (e.g. "about") routes via the --pane=&lt;tag&gt; command-line arg so the
    /// tray's About menu opens straight to the matching NavigationView item.
    /// </summary>
    private void OpenSettings(string? pane = null)
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
            var psi = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
            };
            if (!string.IsNullOrEmpty(pane)) psi.ArgumentList.Add("--pane=" + pane);
            _settingsProcess = Process.Start(psi);
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

    /// <summary>
    /// Swoosh-style silent update: download the matching installer in the background and run it
    /// with /VERYSILENT. The installer's CloseApplications + AppMutex handle file replacement and
    /// the [Run]/Check: WizardSilent line relaunches the tray. There's no modal dialog and no
    /// changelog window — the user sees only the "Downloading…" tray balloon and then the
    /// freshly-updated tray icon when the new version starts.
    /// </summary>
    private async Task ApplyPendingUpdateAsync(UpdateChecker.UpdateInfo info)
    {
        if (_updateInFlight) return;
        _updateInFlight = true;
        try
        {
            LogUpdate($"Apply requested for v{info.Latest.ToString(3)} (installerUrl={info.InstallerUrl ?? "<none>"})");

            if (string.IsNullOrEmpty(info.InstallerUrl))
            {
                // No installer asset on this release — fall back to the GitHub page.
                _tray.ShowBalloonTip(5000, "Hot Corners",
                    "This release didn't ship an installer. Opening the releases page.",
                    ToolTipIcon.Warning);
                OpenReleasesPage(info.HtmlUrl);
                return;
            }

            _tray.ShowBalloonTip(4000, "Hot Corners",
                $"Downloading version {info.Latest.ToString(3)}\u2026",
                ToolTipIcon.Info);

            var path = await UpdateChecker.DownloadInstallerAsync(info.InstallerUrl).ConfigureAwait(true);
            if (path == null || !File.Exists(path))
            {
                LogUpdate("Download returned null or missing file.");
                _tray.ShowBalloonTip(5000, "Hot Corners",
                    "Couldn't download the update. Opening the releases page instead.",
                    ToolTipIcon.Warning);
                OpenReleasesPage(info.HtmlUrl);
                return;
            }

            var sz = new FileInfo(path).Length;
            LogUpdate($"Downloaded {sz} bytes to {path}.");
            if (sz < 1024 * 1024) // sanity: installer is ~100 MB, never under 1 MB
            {
                LogUpdate("Downloaded file is suspiciously small. Aborting install.");
                _tray.ShowBalloonTip(5000, "Hot Corners",
                    "Update download was incomplete. Opening the releases page instead.",
                    ToolTipIcon.Warning);
                OpenReleasesPage(info.HtmlUrl);
                return;
            }

            // /VERYSILENT runs the Inno installer with no progress UI. We deliberately do
            // NOT pass /SUPPRESSMSGBOXES so the close-applications dialog can still flag a
            // problem instead of being silently dismissed in the "cancel" direction (which
            // was making installs vanish without a trace).
            //
            // Critical: we exit the tray BEFORE launching the installer, otherwise the
            // installer has to fight the running tray for file locks via Restart Manager
            // and a /SUPPRESSMSGBOXES failure leaves the user back where they started.
            // The installer is started via ShellExecute (separate elevated process tree),
            // so our process exiting doesn't take the installer down with it.
            try
            {
                Process.Start(new ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    Arguments = "/VERYSILENT",
                });
                LogUpdate("Installer launched. Exiting tray so file replacement can proceed cleanly.");
            }
            catch (Exception ex)
            {
                LogUpdate($"Process.Start failed: {ex.GetType().Name}: {ex.Message}");
                _tray.ShowBalloonTip(5000, "Hot Corners",
                    "Could not start the installer. Opening the releases page instead.",
                    ToolTipIcon.Warning);
                OpenReleasesPage(info.HtmlUrl);
                return;
            }

            // Give the user a beat to see the balloon, then exit. The installer's [Run]
            // section relaunches HotCorners.exe via Check: WizardSilent after install
            // completes, so the tray reappears on its own.
            await Task.Delay(500).ConfigureAwait(true);
            ExitThread();
        }
        finally
        {
            _updateInFlight = false;
        }
    }

    /// <summary>
    /// Append a timestamped line to %TEMP%\HotCorners-update.log so failed updates leave
    /// breadcrumbs the user can share. All errors are swallowed -- logging is best effort.
    /// </summary>
    private static void LogUpdate(string message)
    {
        try
        {
            var logPath = Path.Combine(Path.GetTempPath(), "HotCorners-update.log");
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            File.AppendAllText(logPath, line);
        }
        catch { /* logging must never crash the updater */ }
    }

    private static void OpenReleasesPage(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* shell can't open browser — nothing actionable */ }
    }

    private void EnsureTrayVisible()
    {
        try
        {
            // Force a Shell_NotifyIcon(NIM_ADD) by toggling Visible. The WinForms setter
            // only issues an Add when transitioning false→true, so the toggle is required.
            // Both calls run on the same UI tick — no visible flicker.
            _tray.Visible = false;
            _tray.Visible = true;
        }
        catch { /* shutdown race — fine to swallow */ }
    }

    private void OnPoll(object? sender, EventArgs e)
    {
        if (_paused) { _overlay.Cancel(); return; }
        if (!GetCursorPos(out var pt)) return;

        var (corner, screen) = DetectCorner(pt, _settings);

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
    //
    // Per-corner disable: each (display, corner) pair can be switched off in the
    // Monitors pane (e.g. stacked monitors where the top one only handles top corners
    // and the bottom only handles bottom corners). DisabledMonitorCorners stores those
    // pairs as "<hardware-id>|<Corner>" strings; the hardware ID survives reconnects
    // and dock/undock on the same port.
    //
    // Docking fallback: if every connected display has every corner disabled, arm
    // everything for this session instead of leaving the app silently dead. They can
    // still pause from the tray for an explicit "off" state.
    private static (Corner, Screen?) DetectCorner(POINT pt, AppSettings settings)
    {
        const int tol = 2;
        var all = Screen.AllScreens;
        if (all.Length == 0) return (Corner.None, null);

        var disabled = settings.DisabledMonitorCorners;
        var fallbackToAll = AllCornersDisabledOnEveryConnectedDisplay(all, disabled);

        foreach (var s in all)
        {
            var b = s.Bounds;
            var atLeft = pt.X <= b.Left + tol;
            var atRight = pt.X >= b.Right - 1 - tol;
            var atTop = pt.Y <= b.Top + tol;
            var atBottom = pt.Y >= b.Bottom - 1 - tol;

            if (!((atLeft || atRight) && (atTop || atBottom))) continue;

            // "Blocked" must be evaluated against ALL physical monitors. A disabled
            // corner still lets the cursor escape past the edge of an adjoining
            // display, so the corner can't actually be trapped there.
            var leftBlocked = atLeft && HasNeighborHorizontally(all, s, pt.Y, leftSide: true);
            var rightBlocked = atRight && HasNeighborHorizontally(all, s, pt.Y, leftSide: false);
            var topBlocked = atTop && HasNeighborVertically(all, s, pt.X, topSide: true);
            var bottomBlocked = atBottom && HasNeighborVertically(all, s, pt.X, topSide: false);

            Corner candidate = Corner.None;
            if (atTop && atLeft && !topBlocked && !leftBlocked) candidate = Corner.TopLeft;
            else if (atTop && atRight && !topBlocked && !rightBlocked) candidate = Corner.TopRight;
            else if (atBottom && atLeft && !bottomBlocked && !leftBlocked) candidate = Corner.BottomLeft;
            else if (atBottom && atRight && !bottomBlocked && !rightBlocked) candidate = Corner.BottomRight;
            if (candidate == Corner.None) continue;

            if (!fallbackToAll)
            {
                var hwid = MonitorIdResolver.Resolve(s.DeviceName);
                if (disabled.Contains(CornerKey(hwid, candidate))) continue;
            }
            return (candidate, s);
        }
        return (Corner.None, null);
    }

    /// <summary>True when every connected display has all four corners disabled — the
    /// dock-undock safety net so the app never goes silently dead.</summary>
    private static bool AllCornersDisabledOnEveryConnectedDisplay(Screen[] all, HashSet<string> disabled)
    {
        if (disabled.Count == 0) return false;
        foreach (var s in all)
        {
            var hwid = MonitorIdResolver.Resolve(s.DeviceName);
            foreach (Corner c in Enum.GetValues<Corner>())
            {
                if (c == Corner.None) continue;
                if (!disabled.Contains(CornerKey(hwid, c))) return false;
            }
        }
        return true;
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
            _trayWatchdog.Stop();
            _trayWatchdog.Dispose();
            _taskbarListener.Dispose();
            _updateService.Dispose();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
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

    /// <summary>
    /// Hidden message-only window that listens for Explorer's "TaskbarCreated" broadcast
    /// (sent when the shell tray is created or recreated, e.g. after explorer.exe restarts).
    /// When received, re-adds the NotifyIcon so it doesn't silently vanish from the tray.
    /// </summary>
    private sealed class TaskbarRecreatedListener : NativeWindow, IDisposable
    {
        private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
        private readonly Action _onCreated;

        public TaskbarRecreatedListener(Action onCreated)
        {
            _onCreated = onCreated;
            CreateHandle(new CreateParams { Caption = "HotCornersTaskbarWatcher" });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == TaskbarCreated)
            {
                try { _onCreated(); } catch { /* don't crash the message pump */ }
            }
            base.WndProc(ref m);
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpString);

        public void Dispose() => DestroyHandle();
    }
}
