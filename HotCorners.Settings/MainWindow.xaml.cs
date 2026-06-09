using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using HotCorners;
using HotCorners.Settings;
using HotCorners.Updates;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics;
using Windows.Storage.Streams;
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
        // 640 px preview + 22 px card padding × 2 + 28 px outer margin × 2 + 190 px nav pane
        // + ~24 px chrome ≈ 980 px minimum to render the corners uncropped. Default a bit
        // wider so it doesn't sit flush against the edges.
        ResizeForDpi(1060, 780);
        EnforceMinimumSize(960, 580);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
        if (File.Exists(iconPath))
        {
            try { AppWindow.SetIcon(iconPath); } catch { /* best effort */ }
        }

        // The About page shows the actual app icon (PNG) — load it from the
        // Assets folder copied next to the exe (this is an unpackaged WinUI 3
        // app, so file URIs work directly).
        try
        {
            var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "icon-256.png");
            if (File.Exists(logoPath))
                AboutLogo.Source = new BitmapImage(new Uri(logoPath));
        }
        catch { /* non-fatal: the card just shows no logo */ }

        var version = $"v{UpdateChecker.CurrentVersion.ToString(3)}";
        UpdatesVersionText.Text = $"You're running {version}.";
        AboutVersionText.Text = $"Version {UpdateChecker.CurrentVersion.ToString(3)}";

        PopulateCornerCombos();
        LoadFrom(_store.Current);
        // Both the tray and the settings UI may be the first thing to launch after upgrading
        // from v0.4.x. Migrating in both places guarantees the user's "primary only" pick
        // gets translated into the per-monitor model without depending on launch order.
        MigrateLegacyMonitorMode();

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

        _ = TryLoadWallpaperAsync();
        // The wallpaper file path changes when the user changes their background
        // (Settings → Personalization), so refresh whenever the window regains focus.
        // Also rebuild the monitor picker on activation in case the user plugged in or
        // unplugged a display while the window was in the background.
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) return;
            _ = TryLoadWallpaperAsync();
            if (MonitorsPane.Visibility == Visibility.Visible) BuildMonitorsLayout();
        };
    }

    private void MigrateLegacyMonitorMode()
    {
        var current = _store.Current;
        if (current.MultiMonitor != MultiMonitorMode.PrimaryOnly) return;
        var monitors = MonitorEnumerator.EnumerateAll();
        if (monitors.Count == 0) return;
        var next = current.Clone();
        foreach (var m in monitors)
            if (!m.IsPrimary) next.DisabledMonitors.Add(m.DeviceName);
        next.MultiMonitor = MultiMonitorMode.AllMonitors;
        _store.Save(next);
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

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    // ---- Wallpaper preview ------------------------------------------------

    private const uint SPI_GETDESKWALLPAPER = 0x0073;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, StringBuilder pvParam, uint fWinIni);

    /// <summary>Returns the path of the current desktop wallpaper image, or null if
    /// it can't be determined. Windows always exposes the active background as a
    /// flat image file (Spotlight, Themes, and per-monitor wallpapers all roll up
    /// to a single SPI_GETDESKWALLPAPER value).</summary>
    private static string? GetCurrentWallpaperPath()
    {
        try
        {
            var sb = new StringBuilder(520);
            if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0))
            {
                var path = sb.ToString();
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    return path;
            }
        }
        catch { /* SPI is best-effort; we'll fall back to the gradient. */ }
        return null;
    }

    private string? _lastWallpaperPath;

    private async Task TryLoadWallpaperAsync()
    {
        var path = GetCurrentWallpaperPath();
        if (path == null || string.Equals(path, _lastWallpaperPath, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            // The wallpaper file (especially TranscodedWallpaper) is sometimes held
            // exclusively by Windows. Copy bytes into a memory stream first, then
            // hand a WinRT-compatible random access stream to BitmapImage.
            byte[] bytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var ms = new MemoryStream())
            {
                await fs.CopyToAsync(ms);
                bytes = ms.ToArray();
            }

            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);

            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);

            ScreenBackground.Background = new ImageBrush
            {
                ImageSource = bmp,
                Stretch = Stretch.UniformToFill,
            };
            _lastWallpaperPath = path;
        }
        catch
        {
            // Keep the fallback gradient on any decode/IO failure.
        }
    }

    // ---- Navigation -------------------------------------------------------

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItemContainer as NavigationViewItem)?.Tag as string ?? "corners";
        CornersPane.Visibility = tag == "corners" ? Visibility.Visible : Visibility.Collapsed;
        MonitorsPane.Visibility = tag == "monitors" ? Visibility.Visible : Visibility.Collapsed;
        BehaviorPane.Visibility = tag == "behavior" ? Visibility.Visible : Visibility.Collapsed;
        UpdatesPane.Visibility = tag == "updates" ? Visibility.Visible : Visibility.Collapsed;
        AboutPane.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;

        // Rebuild the picker whenever the user lands on it — cheap, and it picks up any
        // monitor add/remove that happened while the window was open.
        if (tag == "monitors") BuildMonitorsLayout();
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
            ShowOverlayToggle.IsOn = s.ShowCornerOverlay;
        }
        finally
        {
            _loading = false;
        }

        // Refresh the monitor picker too, in case the change toggled a display's
        // armed state from another process (the tray) or a hand-edit of settings.json.
        if (MonitorsPane.Visibility == Visibility.Visible) BuildMonitorsLayout();
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

    private void OnShowOverlayToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var next = _store.Current.Clone();
        next.ShowCornerOverlay = ShowOverlayToggle.IsOn;
        _store.Save(next);
    }

    // ---- Monitors pane ----------------------------------------------------

    /// <summary>
    /// Render a Windows Settings &gt; Display style picker into <c>MonitorsCanvas</c>:
    /// each physical monitor becomes a clickable rounded rectangle positioned at its
    /// real desktop coordinates (uniformly scaled to fit the card). Enabled monitors
    /// glow with the accent color; disabled ones go muted and washed out. A click
    /// flips the entry in <see cref="AppSettings.DisabledMonitors"/> and saves.
    /// </summary>
    private void BuildMonitorsLayout()
    {
        MonitorsCanvas.Children.Clear();
        var monitors = MonitorEnumerator.EnumerateAll();

        if (monitors.Count == 0)
        {
            MonitorsEmpty.Visibility = Visibility.Visible;
            MonitorsCanvas.Width = 0;
            MonitorsCanvas.Height = 0;
            MonitorsSummaryText.Text = "";
            MonitorsResetButton.IsEnabled = false;
            return;
        }
        MonitorsEmpty.Visibility = Visibility.Collapsed;

        // Uniform scale so the whole arrangement fits in the card. The card body is
        // ~660 wide x 300 tall after padding; clamp to leave breathing room around
        // edges. Smallest practical monitor tile is 110 wide so single-display setups
        // still render at a reasonable size.
        var bboxLeft = monitors.Min(m => m.Left);
        var bboxTop = monitors.Min(m => m.Top);
        var bboxRight = monitors.Max(m => m.Right);
        var bboxBottom = monitors.Max(m => m.Bottom);
        var bboxW = Math.Max(1, bboxRight - bboxLeft);
        var bboxH = Math.Max(1, bboxBottom - bboxTop);

        const double targetMaxW = 620;
        const double targetMaxH = 280;
        var scale = Math.Min(targetMaxW / bboxW, targetMaxH / bboxH);
        // Don't blow tiny single-display layouts up so they overflow nor shrink them
        // so small you can't read the number — clamp to a reasonable visual range.
        scale = Math.Min(scale, 0.35);
        scale = Math.Max(scale, 0.04);

        var disabled = _store.Current.DisabledMonitors;
        var armedCount = 0;
        foreach (var m in monitors)
        {
            var isOn = !disabled.Contains(m.DeviceName);
            if (isOn) armedCount++;

            var tileW = Math.Max(90, m.Width * scale);
            var tileH = Math.Max(60, m.Height * scale);
            var left = (m.Left - bboxLeft) * scale;
            var top = (m.Top - bboxTop) * scale;

            var tile = BuildMonitorTile(m, isOn, tileW, tileH);
            Canvas.SetLeft(tile, left);
            Canvas.SetTop(tile, top);
            MonitorsCanvas.Children.Add(tile);
        }

        MonitorsCanvas.Width = bboxW * scale;
        MonitorsCanvas.Height = bboxH * scale;

        var total = monitors.Count;
        MonitorsSummaryText.Text = armedCount == total
            ? $"Hot Corners is armed on all {total} display{(total == 1 ? "" : "s")}."
            : armedCount == 0
                ? $"Hot Corners is off on every display. Click a monitor to turn it back on."
                : $"Hot Corners is armed on {armedCount} of {total} displays.";
        MonitorsResetButton.IsEnabled = armedCount < total;
    }

    private Button BuildMonitorTile(MonitorInfo m, bool isOn, double width, double height)
    {
        var numberText = new TextBlock
        {
            Text = m.Index.ToString(),
            FontSize = Math.Min(48, Math.Max(22, height * 0.42)),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var primaryBadge = new Border
        {
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 1, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(6),
            Visibility = m.IsPrimary ? Visibility.Visible : Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "Primary",
                FontSize = 10,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"],
            },
        };

        var stateGlyph = new FontIcon
        {
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
            Glyph = isOn ? "\uE73E" : "\uE711", // CheckMark / Cancel
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 10, 0),
            Foreground = isOn
                ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
                : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        var resText = new TextBlock
        {
            Text = $"{m.Width}\u00D7{m.Height}",
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 6),
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        var grid = new Grid();
        grid.Children.Add(numberText);
        grid.Children.Add(primaryBadge);
        grid.Children.Add(stateGlyph);
        grid.Children.Add(resText);

        var button = new Button
        {
            Width = width,
            Height = height,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            Content = grid,
            Tag = m.DeviceName,
            Opacity = isOn ? 1.0 : 0.55,
            BorderThickness = new Thickness(isOn ? 2 : 1),
        };
        if (isOn)
        {
            button.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            button.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
        }
        else
        {
            button.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];
            button.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlFillColorDisabledBrush"];
        }

        var tooltipText = string.IsNullOrEmpty(m.FriendlyName)
            ? $"Display {m.Index}{(m.IsPrimary ? " (Primary)" : "")}\n{m.Width}\u00D7{m.Height} at {m.Left},{m.Top}\n\nClick to turn hot corners {(isOn ? "off" : "on")} for this display."
            : $"Display {m.Index} — {m.FriendlyName}{(m.IsPrimary ? " (Primary)" : "")}\n{m.Width}\u00D7{m.Height} at {m.Left},{m.Top}\n\nClick to turn hot corners {(isOn ? "off" : "on")} for this display.";
        ToolTipService.SetToolTip(button, tooltipText);

        button.Click += OnMonitorTileClick;
        return button;
    }

    private void OnMonitorTileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string device) return;
        var next = _store.Current.Clone();
        if (!next.DisabledMonitors.Remove(device))
            next.DisabledMonitors.Add(device);
        _store.Save(next);
        BuildMonitorsLayout();
    }

    private void OnEnableAllMonitors(object sender, RoutedEventArgs e)
    {
        var next = _store.Current.Clone();
        if (next.DisabledMonitors.Count == 0) return;
        next.DisabledMonitors.Clear();
        _store.Save(next);
        BuildMonitorsLayout();
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
