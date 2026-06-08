using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace HotCorners.Overlay;

/// <summary>
/// A click-through, per-pixel-alpha, always-on-top layered window used to render the
/// translucent "hot corner" puddle. The form never receives input — it's only ever
/// a visual indicator that fades in while the cursor dwells in a corner and ripples
/// out when the action fires.
///
/// We use UpdateLayeredWindow + a 32-bpp ARGB bitmap (rather than a TransparencyKey
/// Form) because we need anti-aliased soft edges on the radial gradient and that
/// requires real per-pixel alpha.
/// </summary>
internal sealed class CornerOverlay : Form
{
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020; // click-through
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const uint ULW_ALPHA = 0x02;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    public CornerOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = "";
        AutoScaleMode = AutoScaleMode.None;
        Size = new Size(1, 1);
        Location = new Point(-32000, -32000);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW
                       | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    // Don't steal focus when we appear.
    protected override bool ShowWithoutActivation => true;

    /// <summary>
    /// Push a freshly composed ARGB bitmap into the window at the given screen position.
    /// The bitmap's alpha channel becomes the per-pixel translucency.
    /// </summary>
    public void Apply(Bitmap bitmap, int screenX, int screenY)
    {
        if (bitmap.PixelFormat != PixelFormat.Format32bppArgb)
            throw new ArgumentException("Bitmap must be 32bpp ARGB", nameof(bitmap));

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;

        try
        {
            hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memDc, hBitmap);

            var size = new SIZE(bitmap.Width, bitmap.Height);
            var pointSource = new POINT(0, 0);
            var topPos = new POINT(screenX, screenY);
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA,
            };
            UpdateLayeredWindow(Handle, screenDc, ref topPos, ref size,
                                memDc, ref pointSource, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
            if (hBitmap != IntPtr.Zero)
            {
                SelectObject(memDc, oldBitmap);
                DeleteObject(hBitmap);
            }
            DeleteDC(memDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; public SIZE(int x, int y) { cx = x; cy = y; } }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
        ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc,
        int crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
}
