using System.Drawing.Drawing2D;

namespace HotCorners;

internal sealed class ScreenPreview : Panel
{
    public ScreenPreview()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        DoubleBuffered = true;
        BackColor = Color.Transparent;
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        // Paint parent background so our rounded shape sits cleanly on the form.
        if (Parent != null)
        {
            using var b = new SolidBrush(Parent.BackColor);
            pevent.Graphics.FillRectangle(b, ClientRectangle);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var bounds = ClientRectangle;
        bounds.Inflate(-6, -6);
        const int radius = 20;

        // Drop shadow (soft, offset down by 2px).
        for (var i = 6; i >= 1; i--)
        {
            var shadowRect = new Rectangle(bounds.X - i, bounds.Y - i + 1, bounds.Width + i * 2, bounds.Height + i * 2);
            using var path = RoundRect(shadowRect, radius + i);
            using var brush = new SolidBrush(Color.FromArgb(8, 0, 0, 0));
            g.FillPath(brush, path);
        }

        using (var bodyPath = RoundRect(bounds, radius))
        {
            using var grad = new LinearGradientBrush(
                bounds,
                Color.FromArgb(252, 253, 255),
                Color.FromArgb(229, 233, 239),
                LinearGradientMode.Vertical);
            g.FillPath(grad, bodyPath);

            using var border = new Pen(Color.FromArgb(170, 178, 188), 1f);
            g.DrawPath(border, bodyPath);
        }

        // Inner highlight rim (1px inside, lighter).
        var innerRect = bounds; innerRect.Inflate(-1, -1);
        using (var innerPath = RoundRect(innerRect, radius - 1))
        using (var innerPen = new Pen(Color.FromArgb(100, 255, 255, 255), 1f))
        {
            g.DrawPath(innerPen, innerPath);
        }
    }

    private static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
