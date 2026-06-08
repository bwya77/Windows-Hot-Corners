using HotCorners.Updates;

namespace HotCorners;

/// <summary>
/// Modal "An update is available" prompt shown after a successful update check finds a newer
/// release. Offers Install Now, Skip, or View on GitHub. While installing it shows a download
/// progress bar and then hands off to the installer (which terminates this process to replace
/// its own exe).
/// </summary>
internal sealed class UpdatePromptForm : Form
{
    private readonly UpdateChecker.UpdateInfo _info;
    private readonly ProgressBar _progress;
    private readonly Label _status;
    private readonly Button _install;
    private readonly Button _skip;
    private readonly Button _openWeb;

    public UpdatePromptForm(UpdateChecker.UpdateInfo info)
    {
        _info = info;

        Text = "Hot Corners — Update Available";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 360);
        BackColor = Color.FromArgb(244, 245, 248);
        Font = new Font("Segoe UI", 9.5f);
        Icon = SystemIcons.Information;
        ShowInTaskbar = true;

        var title = new Label
        {
            Text = $"Hot Corners {info.Latest.ToString(3)} is available",
            Font = new Font("Segoe UI Semibold", 13f),
            ForeColor = Color.FromArgb(18, 22, 28),
            AutoSize = true,
            Location = new Point(24, 20),
            BackColor = Color.Transparent,
        };
        var current = new Label
        {
            Text = $"You're running {UpdateChecker.CurrentVersion.ToString(3)}.",
            ForeColor = Color.FromArgb(82, 90, 100),
            AutoSize = true,
            Location = new Point(24, 52),
            BackColor = Color.Transparent,
        };
        Controls.Add(title);
        Controls.Add(current);

        var notesHeader = new Label
        {
            Text = "What's new",
            Font = new Font("Segoe UI Semibold", 10f),
            ForeColor = Color.FromArgb(18, 22, 28),
            AutoSize = true,
            Location = new Point(24, 88),
            BackColor = Color.Transparent,
        };
        Controls.Add(notesHeader);

        var notes = new TextBox
        {
            Text = (info.Notes ?? "").Replace("\n", "\r\n").Trim(),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(24, 112),
            Size = new Size(472, 148),
            BackColor = Color.White,
        };
        if (string.IsNullOrWhiteSpace(notes.Text))
        {
            notes.Text = "(No release notes were published.)";
        }
        Controls.Add(notes);

        _progress = new ProgressBar
        {
            Location = new Point(24, 274),
            Size = new Size(472, 14),
            Style = ProgressBarStyle.Continuous,
            Visible = false,
            Minimum = 0,
            Maximum = 1000,
        };
        _status = new Label
        {
            Location = new Point(24, 294),
            AutoSize = true,
            ForeColor = Color.FromArgb(82, 90, 100),
            BackColor = Color.Transparent,
            Text = "",
        };
        Controls.Add(_progress);
        Controls.Add(_status);

        _install = new Button
        {
            Text = "Install Now",
            Size = new Size(120, 30),
            Location = new Point(376, 318),
            FlatStyle = FlatStyle.System,
        };
        _install.Click += async (_, _) => await InstallAsync();

        _skip = new Button
        {
            Text = "Skip",
            Size = new Size(90, 30),
            Location = new Point(280, 318),
            FlatStyle = FlatStyle.System,
        };
        _skip.Click += (_, _) => Close();

        _openWeb = new Button
        {
            Text = "View on GitHub",
            Size = new Size(130, 30),
            Location = new Point(24, 318),
            FlatStyle = FlatStyle.System,
        };
        _openWeb.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = info.HtmlUrl,
                    UseShellExecute = true,
                });
            }
            catch { /* best effort */ }
        };

        Controls.Add(_install);
        Controls.Add(_skip);
        Controls.Add(_openWeb);

        AcceptButton = _install;
        CancelButton = _skip;

        // No installer asset in the release (rare — releases prior to v0.1.0 didn't ship one).
        // Fall back to opening the GitHub page.
        if (string.IsNullOrEmpty(info.InstallerUrl))
        {
            _install.Enabled = false;
            _status.Text = "No installer attached to this release. Use 'View on GitHub' to download manually.";
        }
    }

    private async Task InstallAsync()
    {
        _install.Enabled = false;
        _skip.Enabled = false;
        _openWeb.Enabled = false;
        _progress.Visible = true;
        _status.Text = "Downloading installer\u2026";

        var progress = new Progress<double>(p =>
        {
            _progress.Value = Math.Min(_progress.Maximum, (int)(p * _progress.Maximum));
            _status.Text = $"Downloading installer\u2026 {(int)(p * 100)}%";
        });

        var launched = await UpdateService.DownloadAndLaunchAsync(_info, progress).ConfigureAwait(true);
        if (launched)
        {
            _status.Text = "Installer launched. Approve the UAC prompt to continue.";
            // Installer will terminate this process; close the dialog so it doesn't look hung.
            await Task.Delay(1000).ConfigureAwait(true);
            Close();
        }
        else
        {
            _status.Text = "Couldn't launch the installer. Try again or use 'View on GitHub'.";
            _install.Enabled = true;
            _skip.Enabled = true;
            _openWeb.Enabled = true;
            _progress.Visible = false;
        }
    }
}
