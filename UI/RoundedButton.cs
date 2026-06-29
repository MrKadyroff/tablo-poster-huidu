using System.Drawing.Drawing2D;

namespace LedImageUpdaterService.UI;

/// <summary>
/// Owner-drawn button with smooth anti-aliased rounded corners, hover/press
/// states and an optional subtle border — a modern "web" look for WinForms.
/// Uses BackColor as the base fill and ForeColor for the text.
/// </summary>
internal sealed class RoundedButton : Button
{
    private bool _hover;
    private bool _down;

    public int CornerRadius { get; set; } = 9;
    public Color BorderColorCustom { get; set; } = Color.Empty;

    public RoundedButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseOverBackColor = Color.Transparent;
        FlatAppearance.MouseDownBackColor = Color.Transparent;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        Cursor = Cursors.Hand;
        BackColor = Color.FromArgb(31, 111, 235);
        ForeColor = Color.White;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    private static Color Lighten(Color c, double a) => Color.FromArgb(c.A,
        (int)Math.Min(255, c.R + (255 - c.R) * a),
        (int)Math.Min(255, c.G + (255 - c.G) * a),
        (int)Math.Min(255, c.B + (255 - c.B) * a));

    private static Color Darken(Color c, double a) => Color.FromArgb(c.A,
        (int)Math.Max(0, c.R * (1 - a)),
        (int)Math.Max(0, c.G * (1 - a)),
        (int)Math.Max(0, c.B * (1 - a)));

    internal static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        if (d <= 0) { path.AddRectangle(r); path.CloseFigure(); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // Blend the rounded corners into the parent background
        g.Clear(Parent?.BackColor ?? BackColor);

        var fill = !Enabled ? Darken(BackColor, 0.25)
                 : _down ? Darken(BackColor, 0.12)
                 : _hover ? Lighten(BackColor, 0.14)
                 : BackColor;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedRect(rect, CornerRadius);

        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);

        var border = BorderColorCustom != Color.Empty ? BorderColorCustom : Lighten(BackColor, 0.22);
        using (var pen = new Pen(border, 1f))
            g.DrawPath(pen, path);

        TextRenderer.DrawText(g, Text, Font, rect,
            Enabled ? ForeColor : Lighten(ForeColor, -0.0),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
