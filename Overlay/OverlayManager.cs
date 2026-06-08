using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using Microsoft.Win32;

namespace HotCorners.Overlay;

/// <summary>
/// Minimal hot-corner indicator: a single soft quarter-circle that fades in while the
/// cursor dwells in the corner and fades out when the action fires or the cursor leaves.
/// No bounce, no ripple, no ring, no sparkle — just a clean opacity curve on one shape.
/// </summary>
internal sealed class OverlayManager : IDisposable
{
    private const int RadiusDip = 220;
    private const int PadDip = 6;

    // Animation timings (independent of user dwell so it always reads cleanly).
    private const int FadeInMs = 320;
    private const int FadeOutFireMs = 360;
    private const int FadeOutCancelMs = 200;
    private const int TickMs = 16;          // ~60 fps

    // Peak opacity of the puddle at full brightness (0..1).
    private const float PeakOpacity = 0.55f;

    // Scale curve: how big the puddle is at the start of fade-in (grows to 1.0)
    // and how big it grows to during the fire fade-out (gentle release).
    private const float StartScale = 0.55f;
    private const float FireEndScale = 1.18f;

    private readonly System.Windows.Forms.Timer _ticker;
    private readonly CornerOverlay _form;
    private readonly Stopwatch _clock = new();

    private State _state = State.Hidden;
    private Corner _corner = Corner.None;
    private Rectangle _screenBounds;
    private long _phaseStartMs;
    private float _heldOpacity;     // opacity reached when leaving a phase
    private float _heldScale = StartScale; // scale reached when leaving a phase
    private bool _disposed;

    public bool Enabled { get; set; } = true;

    public OverlayManager()
    {
        _form = new CornerOverlay();
        _ = _form.Handle;
        _ticker = new System.Windows.Forms.Timer { Interval = TickMs };
        _ticker.Tick += (_, _) => Tick();
        _clock.Start();
    }

    public void BeginDwell(Corner corner, Rectangle screenBounds, int dwellMs)
    {
        if (!Enabled || corner == Corner.None) { Cancel(); return; }
        _corner = corner;
        _screenBounds = screenBounds;
        _state = State.FadeIn;
        _phaseStartMs = _clock.ElapsedMilliseconds;
        _heldOpacity = 0f;
        _heldScale = StartScale;
        if (!_form.Visible) _form.Show();
        _ticker.Start();
        Tick();
    }

    public void PlayRipple()
    {
        // Fade out with a gentle grow — no ripple rings, no flash, just a soft release.
        if (!Enabled || _corner == Corner.None) return;
        SnapshotHeld();
        _state = State.FadeOutFire;
        _phaseStartMs = _clock.ElapsedMilliseconds;
        _ticker.Start();
    }

    public void Cancel()
    {
        if (_state == State.Hidden) return;
        if (_state == State.FadeOutCancel || _state == State.FadeOutFire) return;
        SnapshotHeld();
        _state = State.FadeOutCancel;
        _phaseStartMs = _clock.ElapsedMilliseconds;
        _ticker.Start();
    }

    private void SnapshotHeld()
    {
        if (_state == State.FadeIn)
        {
            var t = Math.Clamp((_clock.ElapsedMilliseconds - _phaseStartMs) / (float)FadeInMs, 0f, 1f);
            var e = EaseOutCubic(t);
            _heldOpacity = e * PeakOpacity;
            _heldScale = StartScale + (1f - StartScale) * e;
        }
        else if (_state == State.Hold)
        {
            _heldOpacity = PeakOpacity;
            _heldScale = 1f;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ticker.Stop();
        _ticker.Dispose();
        _form.Dispose();
    }

    private void Tick()
    {
        if (_disposed) return;

        switch (_state)
        {
            case State.FadeIn:
                {
                    var t = Math.Clamp((_clock.ElapsedMilliseconds - _phaseStartMs) / (float)FadeInMs, 0f, 1f);
                    var e = EaseOutCubic(t);
                    Render(e * PeakOpacity, StartScale + (1f - StartScale) * e);
                    if (t >= 1f) { _state = State.Hold; _phaseStartMs = _clock.ElapsedMilliseconds; _heldScale = 1f; }
                    break;
                }
            case State.Hold:
                {
                    Render(PeakOpacity, 1f);
                    _ticker.Stop();
                    break;
                }
            case State.FadeOutFire:
                {
                    var t = Math.Clamp((_clock.ElapsedMilliseconds - _phaseStartMs) / (float)FadeOutFireMs, 0f, 1f);
                    var e = EaseOutCubic(t);
                    Render(_heldOpacity * (1f - EaseInOut(t)), _heldScale + (FireEndScale - _heldScale) * e);
                    if (t >= 1f) Hide();
                    break;
                }
            case State.FadeOutCancel:
                {
                    var t = Math.Clamp((_clock.ElapsedMilliseconds - _phaseStartMs) / (float)FadeOutCancelMs, 0f, 1f);
                    Render(_heldOpacity * (1f - EaseInOut(t)), _heldScale);
                    if (t >= 1f) Hide();
                    break;
                }
            default:
                _ticker.Stop();
                break;
        }
    }

    private void Hide()
    {
        _state = State.Hidden;
        _corner = Corner.None;
        _ticker.Stop();
        if (_form.Visible) _form.Hide();
    }

    /// <summary>
    /// Draw a single soft radial quarter-circle into a 32-bpp ARGB bitmap and push it
    /// to the layered window at the matching screen corner.
    /// </summary>
    private void Render(float opacity, float scale)
    {
        if (_corner == Corner.None || _screenBounds.Width == 0) return;
        opacity = Math.Clamp(opacity, 0f, 1f);
        scale = Math.Clamp(scale, 0.01f, 2f);

        var dpi = GetDpiScale();
        // Always size the bitmap to the maximum possible scale so growth never clips.
        int maxR = (int)(RadiusDip * dpi * FireEndScale);
        int pad = (int)(PadDip * dpi);
        int bmpSize = maxR + pad;
        int r = (int)(RadiusDip * dpi * scale);

        using var bmp = new Bitmap(bmpSize, bmpSize, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingMode = CompositingMode.SourceOver;
            g.Clear(Color.FromArgb(0, 0, 0, 0));

            if (opacity > 0.005f)
            {
                (int cx, int cy, int startAngle) = _corner switch
                {
                    Corner.TopLeft     => (0,       0,       0),
                    Corner.TopRight    => (bmpSize, 0,      90),
                    Corner.BottomLeft  => (0,       bmpSize, 270),
                    Corner.BottomRight => (bmpSize, bmpSize, 180),
                    _ => (0, 0, 0),
                };

                var (centerColor, edgeColor) = GetPuddleColors(opacity);

                using var path = new GraphicsPath();
                path.AddPie(cx - r, cy - r, r * 2f, r * 2f, startAngle, 90);
                using var brush = new PathGradientBrush(path)
                {
                    CenterPoint = new PointF(cx, cy),
                    CenterColor = centerColor,
                    SurroundColors = new[] { edgeColor },
                    FocusScales = new PointF(0.15f, 0.15f),
                };
                g.FillPath(brush, path);
            }
        }

        (int x, int y) = _corner switch
        {
            Corner.TopLeft     => (_screenBounds.Left,                _screenBounds.Top),
            Corner.TopRight    => (_screenBounds.Right - bmpSize,     _screenBounds.Top),
            Corner.BottomLeft  => (_screenBounds.Left,                _screenBounds.Bottom - bmpSize),
            Corner.BottomRight => (_screenBounds.Right - bmpSize,     _screenBounds.Bottom - bmpSize),
            _ => (0, 0),
        };
        _form.Apply(bmp, x, y);
    }

    /// <summary>Smooth, symmetric S-curve. Used for both fade-in and fade-out so the
    /// curves feel matched.</summary>
    private static float EaseInOut(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Decelerating curve — fast at the start, slows into its rest. Used for
    /// the scale bloom so growth eases in instead of stopping abruptly.</summary>
    private static float EaseOutCubic(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        var u = 1f - t;
        return 1f - u * u * u;
    }

    /// <summary>Pick puddle colors that contrast with the user's Windows theme. Read
    /// the registry on each render (microseconds) so changing the theme takes effect
    /// without restarting the tray.</summary>
    private static (Color center, Color edge) GetPuddleColors(float opacity)
    {
        bool lightTheme = IsLightTheme();
        if (lightTheme)
        {
            // Dark slate-blue puddle for light wallpapers — readable without being harsh.
            return (
                Color.FromArgb((int)(220 * opacity), 30, 70, 140),
                Color.FromArgb(0, 60, 100, 170)
            );
        }
        // White-blue puddle for dark wallpapers (original look).
        return (
            Color.FromArgb((int)(255 * opacity), 235, 245, 255),
            Color.FromArgb(0, 200, 220, 250)
        );
    }

    private static bool IsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // SystemUsesLightTheme reflects taskbar/system theme; AppsUseLightTheme is for apps.
            // We use SystemUsesLightTheme because the puddle sits on top of the desktop, which
            // tracks the system theme more closely than the apps theme.
            var v = key?.GetValue("SystemUsesLightTheme");
            if (v is int i) return i != 0;
        }
        catch { }
        return false; // safe default — dark theme
    }

    private static float GetDpiScale()
    {
        try
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            return g.DpiX / 96f;
        }
        catch { return 1f; }
    }

    private enum State { Hidden, FadeIn, Hold, FadeOutFire, FadeOutCancel }
}
