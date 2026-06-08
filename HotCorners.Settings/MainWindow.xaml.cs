using System.Diagnostics;
using HotCorners;
using HotCorners.Settings;
using HotCorners.Updates;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace HotCorners.SettingsApp;

/// <summary>
/// WinUI 3 settings window for Hot Corners. Mirrors the macOS Hot Corners panel: a
/// rounded "screen" preview with a corner combo box at each of its four corners,
/// plus secondary panes for Behavior, Updates, and About. All writes go through
/// <see cref="SettingsStore"/> which atomically saves settings.json and signals the
/// tray process; reads through the same store stay live via FileSystemWatcher + a
/// named cross-process event, so external edits update the UI immediately too.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly SettingsStore _store = new();
    private readonly UpdateChecker _updater = new();
    private static readonly HotAction[] AllActions = Enum.GetValues<HotAction>();
    private bool _loading;

    public MainWindow()
    {
        InitializeComponent();

        Title = "Hot Corners";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ResizeForDpi(960, 760);
        EnforceMinimumSize(640, 560);

        var version = $"v{UpdateChecker.CurrentVersion.ToString(3)}";
        UpdatesVersionText.Text = $"You're running {version}.";
        AboutVersionText.Text = $"Version {UpdateChecker.CurrentVersion.ToString(3)}";

        PopulateCornerCombos();
        LoadFrom(_store.Current);

        // External changes (e.g. tray app wrote the file) come in on a background thread;
        // marshal onto the UI thread before mutating XAML.
        _store.Changed += OnStoreChanged;
        Closed += (_, _) =>
        {
            _store.Changed -= OnStoreChanged;
            _store.Dispose();
        };

        UpdateCaptionButtonColors();
        RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionButtonColors();
    }

    // ---- Layout helpers ---------------------------------------------------

    private void ResizeForDpi(int logicalWidth, int logicalHeight)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi <= 0 ? 1.0 : dpi / 96.0;
        var appWindow = AppWindow;
        appWindow.Resize(new SizeInt32(
            (int)(logicalWidth * scale),
            (int)(logicalHeight * scale)));
    }

    private void EnforceMinimumSize(int minWidth, int minHeight)
    {
        AppWindow.Changed += (s, e) =>
        {
            if (!e.DidSizeChange) return;
            var dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
            var scale = dpi <= 0 ? 1.0 : dpi / 96.0;
            var minW = (int)(minWidth * scale);
            var minH = (int)(minHeight * scale);
            var sz = s.Size;
            if (sz.Width < minW || sz.Height < minH)
            {
                s.Resize(new SizeInt32(Math.Max(sz.Width, minW), Math.Max(sz.Height, minH)));
            }
        };
    }

    /// <summary>Match the caption button glyphs to the resolved theme so the min/max/close
    /// icons stay legible against the Mica backdrop in both light and dark modes.</summary>
    private void UpdateCaptionButtonColors()
    {
        try
        {
            var titleBar = AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            var fg = RootGrid.ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
            titleBar.ButtonForegroundColor = fg;
            titleBar.ButtonHoverForegroundColor = fg;
            titleBar.ButtonPressedForegroundColor = fg;
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 130, 130, 130);
        }
        catch { /* AppWindow customization is best-effort across builds */ }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    // ---- Navigation -------------------------------------------------------

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItemContainer as NavigationViewItem)?.Tag as string ?? "corners";
        CornersPane.Visibility = tag == "corners" ? Visibility.Visible : Visibility.Collapsed;
        BehaviorPane.Visibility = tag == "behavior" ? Visibility.Visible : Visibility.Collapsed;
        UpdatesPane.Visibility = tag == "updates" ? Visibility.Visible : Visibility.Collapsed;
        AboutPane.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Settings binding -------------------------------------------------

    private void PopulateCornerCombos()
    {
        foreach (var combo in new[] { TopLeftCombo, TopRightCombo, BottomLeftCombo, BottomRightCombo })
        {
            combo.Items.Clear();
            foreach (var a in AllActions)
            {
                combo.Items.Add(ActionLabels.Pretty(a));
            }
        }
    }

    private void LoadFrom(AppSettings s)
    {
        _loading = true;
        try
        {
            TopLeftCombo.SelectedIndex = IndexOf(s.Bindings.GetValueOrDefault(Corner.TopLeft, HotAction.None));
            TopRightCombo.SelectedIndex = IndexOf(s.Bindings.GetValueOrDefault(Corner.TopRight, HotAction.None));
            BottomLeftCombo.SelectedIndex = IndexOf(s.Bindings.GetValueOrDefault(Corner.BottomLeft, HotAction.None));
            BottomRightCombo.SelectedIndex = IndexOf(s.Bindings.GetValueOrDefault(Corner.BottomRight, HotAction.None));

            DwellSlider.Value = Math.Clamp(s.DwellMs, (int)DwellSlider.Minimum, (int)DwellSlider.Maximum);
            DwellValueText.Text = $"{(int)DwellSlider.Value} ms";

            SuppressFullscreenToggle.IsOn = s.SuppressInFullscreen;
            LaunchAtLoginToggle.IsOn = s.LaunchAtLogin;
        }
        finally
        {
            _loading = false;
        }
    }

    private static int IndexOf(HotAction a)
    {
        var idx = Array.IndexOf(AllActions, a);
        return idx >= 0 ? idx : 0;
    }

    private void OnStoreChanged(AppSettings updated)
    {
        // Edits from the tray (or a hand edit) — refresh the UI without re-saving.
        DispatcherQueue.TryEnqueue(() => LoadFrom(updated));
    }

    private void UpdateBinding(Corner corner, ComboBox combo)
    {
        if (_loading) return;
        var idx = combo.SelectedIndex;
        if (idx < 0 || idx >= AllActions.Length) return;

        var next = _store.Current.Clone();
        next.Bindings[corner] = AllActions[idx];
        _store.Save(next);
    }

    private void OnTopLeftChanged(object sender, SelectionChangedEventArgs e) => UpdateBinding(Corner.TopLeft, TopLeftCombo);
    private void OnTopRightChanged(object sender, SelectionChangedEventArgs e) => UpdateBinding(Corner.TopRight, TopRightCombo);
    private void OnBottomLeftChanged(object sender, SelectionChangedEventArgs e) => UpdateBinding(Corner.BottomLeft, BottomLeftCombo);
    private void OnBottomRightChanged(object sender, SelectionChangedEventArgs e) => UpdateBinding(Corner.BottomRight, BottomRightCombo);

    private void OnDwellChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var ms = (int)Math.Round(e.NewValue);
        if (DwellValueText != null) DwellValueText.Text = $"{ms} ms";

        if (_loading) return;
        var next = _store.Current.Clone();
        next.DwellMs = ms;
        _store.Save(next);
    }

    private void OnSuppressToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var next = _store.Current.Clone();
        next.SuppressInFullscreen = SuppressFullscreenToggle.IsOn;
        _store.Save(next);
    }

    private void OnLaunchAtLoginToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var next = _store.Current.Clone();
        next.LaunchAtLogin = LaunchAtLoginToggle.IsOn;
        _store.Save(next);
        // The tray app owns the HKCU Run key; it'll mirror this change on the next reload.
    }

    // ---- Updates pane -----------------------------------------------------

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        var originalText = CheckUpdatesButton.Content as string ?? "Check for Updates";
        CheckUpdatesButton.Content = "Checking\u2026";
        UpdatesInfoBar.IsOpen = false;

        try
        {
            var info = await _updater.CheckAsync();
            if (info == null)
            {
                UpdatesInfoBar.Severity = InfoBarSeverity.Success;
                UpdatesInfoBar.Title = "You're up to date";
                UpdatesInfoBar.Message = $"Hot Corners {UpdateChecker.CurrentVersion.ToString(3)} is the latest version.";
                UpdatesInfoBar.IsOpen = true;
            }
            else
            {
                UpdatesInfoBar.Severity = InfoBarSeverity.Informational;
                UpdatesInfoBar.Title = $"Hot Corners {info.Latest.ToString(3)} is available";
                UpdatesInfoBar.Message = "Right-click the tray icon → Check for Updates… to install it, or use the link below.";
                UpdatesInfoBar.IsOpen = true;
                _ = info.HtmlUrl;
            }
        }
        catch
        {
            UpdatesInfoBar.Severity = InfoBarSeverity.Warning;
            UpdatesInfoBar.Title = "Couldn't check for updates";
            UpdatesInfoBar.Message = "Try again in a moment.";
            UpdatesInfoBar.IsOpen = true;
        }
        finally
        {
            CheckUpdatesButton.Content = originalText;
            CheckUpdatesButton.IsEnabled = true;
        }
    }
}
