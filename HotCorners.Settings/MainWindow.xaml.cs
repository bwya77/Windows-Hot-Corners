using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using HotCorners;
using HotCorners.Monitors;
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

        // Honor a "--pane=<tag>" command-line arg so the tray's About menu (and any other
        // future deep-link) can open the settings window directly to a specific pane.
        SelectStartupPane();

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
        var monitors = MonitorEnumerator.EnumerateAll();
        var changed = false;
        var migrated = current.Clone();

        // 1. v0.4.x PrimaryOnly radio -> per-monitor disabled set.
        if (migrated.MultiMonitor == MultiMonitorMode.PrimaryOnly && monitors.Count > 0)
        {
            foreach (var m in monitors)
                if (!m.IsPrimary) migrated.DisabledMonitors.Add(m.HardwareId);
            migrated.MultiMonitor = MultiMonitorMode.AllMonitors;
            changed = true;
        }

        // 2. v0.5.0 stored \\.\DISPLAYn keys. Upgrade any such entries to their stable
        //    hardware ID, dropping entries that don't currently resolve (the matching
        //    physical display isn't connected; the session-only key is meaningless
        //    without it). v0.5.1+ entries are hardware IDs and survive reconnects.
        var legacy = migrated.DisabledMonitors.Where(MonitorIdResolver.IsLegacyAdapterKey).ToList();
        if (legacy.Count > 0)
        {
            foreach (var key in legacy)
            {
                migrated.DisabledMonitors.Remove(key);
                var match = monitors.FirstOrDefault(m =>
                    string.Equals(m.AdapterDeviceName, key, StringComparison.OrdinalIgnoreCase));
                if (match != null && !string.IsNullOrEmpty(match.HardwareId))
                    migrated.DisabledMonitors.Add(match.HardwareId);
            }
            changed = true;
        }

        // 3. v0.5.1 stored whole-monitor entries in DisabledMonitors. v0.5.2 moved to
        //    per-corner control; expand each whole-monitor entry into all four corner
        //    keys so the user's preference becomes "all four corners of this display
        //    off" with no change in observable behavior, then empty the legacy set.
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

        if (changed) _store.Save(migrated);
    }

    private static string CornerKey(string hardwareId, Corner corner) => hardwareId + "|" + corner;

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
    private BitmapImage? _wallpaperBitmap;

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
            _wallpaperBitmap = bmp;
            _lastWallpaperPath = path;

            // Repaint the monitor picker so each tile picks up the new wallpaper.
            if (MonitorsPane.Visibility == Visibility.Visible) BuildMonitorsLayout();
        }
        catch
        {
            // Keep the fallback gradient on any decode/IO failure.
        }
    }

    /// <summary>Parse command-line args for a <c>--pane=&lt;tag&gt;</c> override so the
    /// tray can open the window directly to a specific NavigationView item (e.g. About).
    /// Unknown or missing arg leaves the default "Corners" selection alone.</summary>
    private void SelectStartupPane()
    {
        try
        {
            string? requested = null;
            foreach (var raw in Environment.GetCommandLineArgs())
            {
                if (raw is null) continue;
                const string prefix = "--pane=";
                if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    requested = raw[prefix.Length..].Trim().ToLowerInvariant();
                    break;
                }
            }
            if (string.IsNullOrEmpty(requested)) return;

            // The XAML marks "Corners" with IsSelected="True" so the window has a sane
            // default when launched normally. When we override to a different pane we
            // have to explicitly clear IsSelected on every other item, otherwise WinUI
            // leaves the original highlight in place and the user sees two highlighted
            // items (the XAML-set Corners and the code-set target).
            NavigationViewItem? target = null;
            foreach (var item in Nav.MenuItems)
            {
                if (item is not NavigationViewItem nv) continue;
                if ((nv.Tag as string)?.Equals(requested, StringComparison.OrdinalIgnoreCase) == true)
                    target = nv;
                else
                    nv.IsSelected = false;
            }
            if (target != null)
            {
                target.IsSelected = true;
                Nav.SelectedItem = target;
            }
        }
        catch { /* best effort — falls through to default Corners pane */ }
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
    /// each physical monitor becomes a rounded tile at its real desktop coordinates,
    /// with four small toggles - one in each corner - that switch hot corners on or
    /// off for just that (display, corner) pair. Stacked-monitor users can keep top
    /// corners on the top display and bottom corners on the bottom display. The
    /// user's actual wallpaper is sliced per-monitor so the picker mirrors what's
    /// on screen. All writes go through <see cref="AppSettings.DisabledMonitorCorners"/>
    /// keyed by stable hardware ID.
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

        var bboxLeft = monitors.Min(m => m.Left);
        var bboxTop = monitors.Min(m => m.Top);
        var bboxRight = monitors.Max(m => m.Right);
        var bboxBottom = monitors.Max(m => m.Bottom);
        var bboxW = Math.Max(1, bboxRight - bboxLeft);
        var bboxH = Math.Max(1, bboxBottom - bboxTop);

        const double targetMaxW = 620;
        const double targetMaxH = 280;
        var scale = Math.Min(targetMaxW / bboxW, targetMaxH / bboxH);
        scale = Math.Min(scale, 0.35);
        scale = Math.Max(scale, 0.04);

        var disabled = _store.Current.DisabledMonitorCorners;
        var armedCorners = 0;
        var totalCorners = monitors.Count * 4;

        foreach (var m in monitors)
        {
            var tileW = Math.Max(120, m.Width * scale);
            var tileH = Math.Max(78, m.Height * scale);
            var left = (m.Left - bboxLeft) * scale;
            var top = (m.Top - bboxTop) * scale;

            foreach (Corner c in Enum.GetValues<Corner>())
            {
                if (c == Corner.None) continue;
                if (!disabled.Contains(CornerKey(m.HardwareId, c))) armedCorners++;
            }

            var tile = BuildMonitorTile(m, tileW, tileH,
                wallpaperWidth: bboxW * scale,
                wallpaperHeight: bboxH * scale,
                wallpaperOffsetX: -((m.Left - bboxLeft) * scale),
                wallpaperOffsetY: -((m.Top - bboxTop) * scale),
                disabled: disabled);
            Canvas.SetLeft(tile, left);
            Canvas.SetTop(tile, top);
            MonitorsCanvas.Children.Add(tile);
        }

        MonitorsCanvas.Width = bboxW * scale;
        MonitorsCanvas.Height = bboxH * scale;

        MonitorsSummaryText.Text = armedCorners == totalCorners
            ? $"Hot Corners is armed on every corner of every display ({totalCorners} total)."
            : armedCorners == 0
                ? "Every corner is off. Click a corner dot to turn it back on."
                : $"Hot Corners is armed on {armedCorners} of {totalCorners} corners. Click any corner dot to toggle it.";
        MonitorsResetButton.IsEnabled = armedCorners < totalCorners;
    }

    private Grid BuildMonitorTile(MonitorInfo m, double width, double height,
        double wallpaperWidth, double wallpaperHeight, double wallpaperOffsetX, double wallpaperOffsetY,
        HashSet<string> disabled)
    {
        // A corner counts as "armed" when its key is NOT in DisabledMonitorCorners.
        // We compute the four states up front so the tile chrome (frame brightness,
        // darken intensity) reflects the aggregate.
        var armed = new Dictionary<Corner, bool>();
        foreach (Corner c in Enum.GetValues<Corner>())
        {
            if (c == Corner.None) continue;
            armed[c] = !disabled.Contains(CornerKey(m.HardwareId, c));
        }
        var anyOn = armed.Values.Any(v => v);
        var allOff = !anyOn;

        // ---- Wallpaper slice (or fallback color) inside a CornerRadius-clipped Border.
        var bgFill = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
        };
        if (_wallpaperBitmap != null && wallpaperWidth > 0 && wallpaperHeight > 0)
        {
            var canvas = new Canvas { Width = width, Height = height };
            var img = new Image
            {
                Source = _wallpaperBitmap,
                Width = wallpaperWidth,
                Height = wallpaperHeight,
                Stretch = Stretch.UniformToFill,
            };
            Canvas.SetLeft(img, wallpaperOffsetX);
            Canvas.SetTop(img, wallpaperOffsetY);
            canvas.Children.Add(img);
            bgFill.Child = canvas;
        }

        // Darken intensity scales with how many corners are off so the visual weight of
        // "this display is mostly disabled" matches the state without needing a label.
        var offCount = armed.Values.Count(v => !v);
        var darkAlpha = (byte)(60 + offCount * 30); // 60 (all on) .. 180 (all off)
        var darken = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(darkAlpha, 0, 0, 0)),
        };

        var numberText = new TextBlock
        {
            Text = m.Index.ToString(),
            FontSize = Math.Min(48, Math.Max(22, height * 0.42)),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Colors.White),
        };

        var primaryBadge = new Border
        {
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 1, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = m.IsPrimary ? Visibility.Visible : Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "Primary",
                FontSize = 10,
                Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"],
            },
        };

        var resText = new TextBlock
        {
            Text = $"{m.Width}\u00D7{m.Height}",
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 6),
            Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
        };

        // ---- Outer frame: accent border when any corner is armed, muted otherwise.
        var frame = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(anyOn ? 2 : 1),
            BorderBrush = anyOn
                ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
        };

        var grid = new Grid { Width = width, Height = height };
        grid.Children.Add(bgFill);
        grid.Children.Add(darken);
        grid.Children.Add(primaryBadge);
        grid.Children.Add(numberText);
        grid.Children.Add(resText);
        grid.Children.Add(frame);

        // ---- Four corner toggles. Each is a small circular Border positioned in the
        //      respective corner of the tile. PointerPressed (not Click) keeps the hit
        //      target tight and avoids interfering with the surrounding visuals.
        grid.Children.Add(BuildCornerToggle(m, Corner.TopLeft,
            HorizontalAlignment.Left, VerticalAlignment.Top, armed[Corner.TopLeft]));
        grid.Children.Add(BuildCornerToggle(m, Corner.TopRight,
            HorizontalAlignment.Right, VerticalAlignment.Top, armed[Corner.TopRight]));
        grid.Children.Add(BuildCornerToggle(m, Corner.BottomLeft,
            HorizontalAlignment.Left, VerticalAlignment.Bottom, armed[Corner.BottomLeft]));
        grid.Children.Add(BuildCornerToggle(m, Corner.BottomRight,
            HorizontalAlignment.Right, VerticalAlignment.Bottom, armed[Corner.BottomRight]));

        var nameLabel = string.IsNullOrEmpty(m.FriendlyName)
            ? $"Display {m.Index}"
            : $"Display {m.Index} - {m.FriendlyName}";
        var tooltipText = $"{nameLabel}{(m.IsPrimary ? " (Primary)" : "")}\n{m.Width}\u00D7{m.Height} at {m.Left},{m.Top}\n\nClick the tile to toggle the whole display, or click a corner dot to toggle just that corner.";
        ToolTipService.SetToolTip(grid, tooltipText);

        // ---- Whole-tile click toggles all four corners at once. The corner dots above
        //      mark their PointerPressed as Handled so a click on a dot never bubbles up
        //      here. anyOn -> turn everything off; allOff -> turn everything back on.
        grid.Tag = m.HardwareId;
        grid.PointerPressed += OnMonitorTilePressed;
        return grid;
    }

    private Border BuildCornerToggle(MonitorInfo m, Corner corner, HorizontalAlignment hAlign,
        VerticalAlignment vAlign, bool isOn)
    {
        const double diameter = 22;
        var dot = new Border
        {
            Width = diameter,
            Height = diameter,
            CornerRadius = new CornerRadius(diameter / 2),
            Margin = new Thickness(6),
            HorizontalAlignment = hAlign,
            VerticalAlignment = vAlign,
            Background = new SolidColorBrush(isOn
                ? Color.FromArgb(255, 16, 124, 16)   // green when on
                : Color.FromArgb(220, 40, 40, 40)),  // dim when off
            BorderThickness = new Thickness(1.5),
            BorderBrush = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            Tag = CornerKey(m.HardwareId, corner),
        };

        // Inner check glyph or X depending on state; centered.
        var glyph = new FontIcon
        {
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
            Glyph = isOn ? "\uE73E" : "\uE711", // CheckMark / Cancel
            FontSize = 11,
            Foreground = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        dot.Child = glyph;

        dot.PointerPressed += OnCornerTogglePressed;
        ToolTipService.SetToolTip(dot, $"{corner} corner: {(isOn ? "ON" : "OFF")} (click to toggle)");
        return dot;
    }

    private void OnCornerTogglePressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Border border || border.Tag is not string key) return;
        var next = _store.Current.Clone();
        if (!next.DisabledMonitorCorners.Remove(key))
            next.DisabledMonitorCorners.Add(key);
        _store.Save(next);
        BuildMonitorsLayout();
    }

    private void OnMonitorTilePressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // Corner dots set e.Handled = true in OnCornerTogglePressed, so a click on a
        // dot never reaches this handler -- only clicks on the tile body do.
        if (sender is not Grid grid || grid.Tag is not string hwid) return;
        e.Handled = true;
        var next = _store.Current.Clone();
        var keys = Enum.GetValues<Corner>()
            .Where(c => c != Corner.None)
            .Select(c => CornerKey(hwid, c))
            .ToList();
        var anyArmed = keys.Any(k => !next.DisabledMonitorCorners.Contains(k));
        if (anyArmed)
        {
            // Anything on -> turn the whole display off.
            foreach (var k in keys) next.DisabledMonitorCorners.Add(k);
        }
        else
        {
            // Everything off -> turn the whole display back on.
            foreach (var k in keys) next.DisabledMonitorCorners.Remove(k);
        }
        _store.Save(next);
        BuildMonitorsLayout();
    }

    private void OnEnableAllMonitors(object sender, RoutedEventArgs e)
    {
        var next = _store.Current.Clone();
        if (next.DisabledMonitorCorners.Count == 0 && next.DisabledMonitors.Count == 0) return;
        next.DisabledMonitorCorners.Clear();
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
