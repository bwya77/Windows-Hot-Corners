namespace HotCorners;

internal sealed class SettingsForm : Form
{
    private static readonly int[] DwellPresets = { 25, 50, 100, 150, 250, 400, 600 };
    private static readonly HotAction[] AllActions = Enum.GetValues<HotAction>();

    private readonly Settings _settings;
    private readonly ComboBox _topLeft;
    private readonly ComboBox _topRight;
    private readonly ComboBox _bottomLeft;
    private readonly ComboBox _bottomRight;
    private readonly ComboBox _dwell;
    private readonly CheckBox _suppressFullscreen;
    private readonly CheckBox _runAtLogin;

    public SettingsForm(Settings settings)
    {
        _settings = settings;

        Text = "Hot Corners";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(600, 620);
        BackColor = Color.FromArgb(244, 245, 248);
        Font = new Font("Segoe UI", 9.5f);
        Icon = SystemIcons.Application;

        var title = new Label
        {
            Text = "Hot Corners",
            Font = new Font("Segoe UI Semibold", 15f),
            ForeColor = Color.FromArgb(18, 22, 28),
            AutoSize = true,
            Location = new Point(36, 24),
            BackColor = Color.Transparent,
        };

        var desc = new Label
        {
            Text = "Choose an action for each corner of your screen.",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = Color.FromArgb(82, 90, 100),
            AutoSize = true,
            Location = new Point(36, 58),
            BackColor = Color.Transparent,
        };

        Controls.Add(title);
        Controls.Add(desc);

        var preview = new ScreenPreview
        {
            Location = new Point(36, 96),
            Size = new Size(528, 300),
            BackColor = Color.Transparent,
        };
        Controls.Add(preview);

        _topLeft = MakeCornerCombo();
        _topRight = MakeCornerCombo();
        _bottomLeft = MakeCornerCombo();
        _bottomRight = MakeCornerCombo();

        const int comboW = 170;
        const int comboH = 26;
        const int inset = 26;

        _topLeft.Location = new Point(inset, inset);
        _topRight.Location = new Point(preview.Width - inset - comboW, inset);
        _bottomLeft.Location = new Point(inset, preview.Height - inset - comboH);
        _bottomRight.Location = new Point(preview.Width - inset - comboW, preview.Height - inset - comboH);

        preview.Controls.Add(_topLeft);
        preview.Controls.Add(_topRight);
        preview.Controls.Add(_bottomLeft);
        preview.Controls.Add(_bottomRight);

        BindCorner(_topLeft, Corner.TopLeft);
        BindCorner(_topRight, Corner.TopRight);
        BindCorner(_bottomLeft, Corner.BottomLeft);
        BindCorner(_bottomRight, Corner.BottomRight);

        var behaviorHeader = new Label
        {
            Text = "Behavior",
            Font = new Font("Segoe UI Semibold", 10.5f),
            ForeColor = Color.FromArgb(18, 22, 28),
            AutoSize = true,
            Location = new Point(36, 416),
            BackColor = Color.Transparent,
        };
        Controls.Add(behaviorHeader);

        var dwellLabel = new Label
        {
            Text = "Dwell time:",
            AutoSize = true,
            Location = new Point(36, 450),
            BackColor = Color.Transparent,
        };
        _dwell = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Location = new Point(120, 447),
            Size = new Size(110, 24),
        };
        foreach (var ms in DwellPresets)
        {
            _dwell.Items.Add($"{ms} ms");
        }
        var dwellIndex = Array.IndexOf(DwellPresets, _settings.DwellMs);
        _dwell.SelectedIndex = dwellIndex >= 0 ? dwellIndex : 3; // default 150 ms
        _dwell.SelectedIndexChanged += (_, _) =>
        {
            _settings.DwellMs = DwellPresets[_dwell.SelectedIndex];
            _settings.Save();
        };

        var dwellHelp = new Label
        {
            Text = "How long the cursor must rest in a corner before firing.",
            AutoSize = true,
            Location = new Point(244, 450),
            ForeColor = Color.FromArgb(120, 128, 136),
            BackColor = Color.Transparent,
        };
        Controls.Add(dwellLabel);
        Controls.Add(_dwell);
        Controls.Add(dwellHelp);

        _suppressFullscreen = new CheckBox
        {
            Text = "Don't trigger when a fullscreen app is in the foreground",
            Checked = _settings.SuppressInFullscreen,
            AutoSize = true,
            Location = new Point(36, 492),
            BackColor = Color.Transparent,
        };
        _suppressFullscreen.CheckedChanged += (_, _) =>
        {
            _settings.SuppressInFullscreen = _suppressFullscreen.Checked;
            _settings.Save();
        };
        Controls.Add(_suppressFullscreen);

        _runAtLogin = new CheckBox
        {
            Text = "Start Hot Corners when I sign in to Windows",
            Checked = StartupRegistration.IsEnabled(),
            AutoSize = true,
            Location = new Point(36, 522),
            BackColor = Color.Transparent,
        };
        _runAtLogin.CheckedChanged += (_, _) =>
            StartupRegistration.SetEnabled(_runAtLogin.Checked);
        Controls.Add(_runAtLogin);

        var done = new Button
        {
            Text = "Done",
            Size = new Size(100, 30),
            Location = new Point(ClientSize.Width - 100 - 36, ClientSize.Height - 30 - 24),
            FlatStyle = FlatStyle.System,
        };
        done.Click += (_, _) => Close();
        Controls.Add(done);
        AcceptButton = done;
    }

    private static ComboBox MakeCornerCombo() => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat,
        Size = new Size(170, 26),
        Font = new Font("Segoe UI", 9.25f),
        BackColor = Color.White,
    };

    private void BindCorner(ComboBox cb, Corner corner)
    {
        cb.Items.Clear();
        foreach (var a in AllActions)
        {
            cb.Items.Add(ActionLabels.Pretty(a));
        }

        var current = _settings.Bindings.TryGetValue(corner, out var v) ? v : HotAction.None;
        var idx = Array.IndexOf(AllActions, current);
        cb.SelectedIndex = idx >= 0 ? idx : 0;

        cb.SelectedIndexChanged += (_, _) =>
        {
            _settings.Bindings[corner] = AllActions[cb.SelectedIndex];
            _settings.Save();
        };
    }
}
