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

        _ = TryLoadWallpaperAsync();
        // The wallpaper file path changes when the user changes their background
        // (Settings → Personalization), so refresh whenever the window regains focus.
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
                _ = TryLoadWallpaperAsync();
        };
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
