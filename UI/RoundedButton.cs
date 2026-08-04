using System.Drawing.Drawing2D;

namespace LedImageUpdaterService.UI;

/// <summary>
/// Owner-drawn button with smooth anti-aliased rounded corners, hover/press
/// states and an optional subtle border — a modern "web" look for WinForms.
/// Uses BackColor as the base fill and ForeColor for the text.
///
/// Paints like a CSS button: a soft vertical gradient, a 1px top highlight,
/// a drop shadow that grows on hover ("lift") and collapses on press, and a
/// focus ring for keyboard users.
/// </summary>
internal sealed class RoundedButton : Button
{
    private bool _hover;
    private bool _down;

    /// <summary>Visual weight, mirroring the usual web button variants.</summary>
    internal enum ButtonVariant
    {
        /// <summary>Filled, gradient, drop shadow — the primary call to action.</summary>
        Solid,
        /// <summary>Muted fill with a visible border — secondary action.</summary>
        Ghost,
    }

    public int CornerRadius { get; set; } = 10;
    public Color BorderColorCustom { get; set; } = Color.Empty;
    public ButtonVariant Variant { get; set; } = ButtonVariant.Solid;

    /// <summary>Drop shadow under the button (turn off for dense toolbar buttons).</summary>
    public bool Elevated { get; set; } = true;

    public RoundedButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseOverBackColor = Color.Transparent;
        FlatAppearance.MouseDownBackColor = Color.Transparent;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        Cursor = Cursors.Hand;
        BackColor = UITheme.Accent2;
        ForeColor = Color.White;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

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

        bool ghost = Variant == ButtonVariant.Ghost;
        var baseColor = !Enabled ? UITheme.Mix(BackColor, UITheme.Panel, 0.55)
                      : _down ? UITheme.Darken(BackColor, 0.14)
                      : _hover ? UITheme.Lighten(BackColor, 0.12)
                      : BackColor;

        // Press sinks the face by 1px; hover lifts the shadow.
        int shadow = !Elevated || ghost || !Enabled ? 0 : _down ? 1 : _hover ? 4 : 2;
        var rect = new Rectangle(0, shadow > 0 && _down ? 1 : 0, Width - 1, Height - 1 - shadow);
        if (rect.Height <= 0) rect = new Rectangle(0, 0, Width - 1, Math.Max(1, Height - 1));

        for (int i = shadow; i >= 1; i--)
        {
            // Soft shadow: stacked translucent rounded rects below the face.
            var sr = new Rectangle(rect.X + 1, rect.Y + i, rect.Width - 2, rect.Height);
            using var sp = RoundedRect(sr, CornerRadius);
            using var sb = new SolidBrush(Color.FromArgb(18 + 6 * (shadow - i), 0, 0, 0));
            g.FillPath(sb, sp);
        }

        using var path = RoundedRect(rect, CornerRadius);

        if (ghost)
        {
            var fill = _down ? UITheme.Lighten(UITheme.Panel, 0.10)
                     : _hover ? UITheme.Lighten(UITheme.Panel, 0.06)
                     : UITheme.Panel;
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
        }
        else
        {
            // Vertical gradient — the subtle top-lit look of a web button.
            using var brush = new LinearGradientBrush(
                new Rectangle(rect.X, rect.Y, rect.Width, Math.Max(1, rect.Height)),
                UITheme.Lighten(baseColor, 0.10), UITheme.Darken(baseColor, 0.08),
                LinearGradientMode.Vertical);
            g.FillPath(brush, path);

            // 1px inner highlight along the top edge
            using var hi = new Pen(Color.FromArgb(60, 255, 255, 255), 1f);
            g.DrawLine(hi, rect.X + CornerRadius, rect.Y + 1, rect.Right - CornerRadius, rect.Y + 1);
        }

        var border = BorderColorCustom != Color.Empty ? BorderColorCustom
                   : ghost ? (_hover ? UITheme.Accent : UITheme.Border)
                   : UITheme.Lighten(baseColor, 0.20);
        using (var pen = new Pen(border, 1f))
            g.DrawPath(pen, path);

        if (Focused && Enabled)
        {
            var fr = Rectangle.Inflate(rect, 1, 1);
            using var fp = RoundedRect(fr, CornerRadius + 1);
            using var fpen = new Pen(Color.FromArgb(120, UITheme.Accent), 2f);
            g.DrawPath(fpen, fp);
        }

        TextRenderer.DrawText(g, Text, Font, rect,
            Enabled ? ForeColor : UITheme.TextDim,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
