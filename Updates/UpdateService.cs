using System.Diagnostics;

namespace HotCorners.Updates;

/// <summary>
/// Coordinates the in-app update flow: kicks off a background check ~30s after launch and then
/// every 24 hours, surfaces a tray balloon when a newer release is published, and on user
/// confirmation downloads the installer and launches it (which closes the running app, replaces
/// files, and relaunches via the installer's [Run] section).
/// </summary>
internal sealed class UpdateService : IDisposable
{
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _periodic;
    private readonly UpdateChecker _checker = new();
    private readonly Action<UpdateChecker.UpdateInfo> _onUpdateFound;
    private UpdateChecker.UpdateInfo? _pending;

    /// <summary>Whatever the latest background check turned up, or null if none.
    /// Used by the tray menu to show the "Update to vX.Y.Z" item.</summary>
    public UpdateChecker.UpdateInfo? PendingUpdate => _pending;

    public UpdateService(NotifyIcon tray, Action<UpdateChecker.UpdateInfo> onUpdateFound)
    {
        _tray = tray;
        _onUpdateFound = onUpdateFound;
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_pending != null) _onUpdateFound(_pending);
        };

        _periodic = new System.Windows.Forms.Timer { Interval = (int)TimeSpan.FromHours(24).TotalMilliseconds };
        _periodic.Tick += (_, _) => _ = CheckAsync(showIfUpToDate: false);
    }

    public void StartBackgroundChecks()
    {
        var startup = new System.Windows.Forms.Timer { Interval = 30_000 };
        startup.Tick += (_, _) =>
        {
            startup.Stop();
            startup.Dispose();
            _ = CheckAsync(showIfUpToDate: false);
            _periodic.Start();
        };
        startup.Start();
    }

    public async Task CheckAsync(bool showIfUpToDate)
    {
        var info = await _checker.CheckAsync().ConfigureAwait(true);
        if (info == null)
        {
            if (showIfUpToDate)
            {
                MessageBox.Show(
                    $"You're running the latest version (v{UpdateChecker.CurrentVersion.ToString(3)}).",
                    "Hot Corners",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            return;
        }

        _pending = info;
        _tray.ShowBalloonTip(
            8000,
            "Hot Corners update available",
            $"Version {info.Latest.ToString(3)} is available. Click to install.",
            ToolTipIcon.Info);

        if (showIfUpToDate)
        {
            _onUpdateFound(info);
        }
    }

    /// <summary>
    /// Download the matching installer to %TEMP% and launch it elevated. The installer's
    /// CloseApplications setting will terminate the running app so its files can be replaced,
    /// then relaunch it via the [Run] section. Returns true if the installer was launched.
    /// </summary>
    public static async Task<bool> DownloadAndLaunchAsync(UpdateChecker.UpdateInfo info, IProgress<double>? progress = null)
    {
        if (string.IsNullOrEmpty(info.InstallerUrl)) return false;

        var path = await UpdateChecker.DownloadInstallerAsync(info.InstallerUrl, progress).ConfigureAwait(true);
        if (path == null) return false;

        try
        {
            var psi = new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "/SILENT",
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _periodic.Stop();
        _periodic.Dispose();
    }
}
