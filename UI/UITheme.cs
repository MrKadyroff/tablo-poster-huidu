using System.Drawing.Drawing2D;

namespace LedImageUpdaterService.UI;

/// <summary>
/// Dark "futuristic" theme: deep slate backgrounds with a neon cyan accent.
/// Applied recursively to the settings form so individual builders stay simple.
/// </summary>
internal static class UITheme
{
    public static readonly Color Bg = Color.FromArgb(14, 18, 24);        // form background
    public static readonly Color Panel = Color.FromArgb(20, 26, 34);     // panels / footer
    public static readonly Color Input = Color.FromArgb(27, 34, 44);     // text/list/combo
    public static readonly Color Border = Color.FromArgb(40, 50, 62);
    public static readonly Color Accent = Color.FromArgb(0, 229, 200);   // neon cyan/teal
    public static readonly Color Accent2 = Color.FromArgb(31, 111, 235); // electric blue
    public static readonly Color Text = Color.FromArgb(214, 222, 231);
    public static readonly Color TextDim = Color.FromArgb(140, 150, 165);

    /// <summary>Recursively applies the dark theme to a control tree.</summary>
    public static void Apply(Control root)
    {
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case TextBox tb:
                    tb.BackColor = Input; tb.ForeColor = Text; tb.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case ListBox lb:
                    lb.BackColor = Input; lb.ForeColor = Text; lb.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case ComboBox cb:
                    cb.BackColor = Input; cb.ForeColor = Text; cb.FlatStyle = FlatStyle.Flat;
                    break;
                case NumericUpDown nud:
                    nud.BackColor = Input; nud.ForeColor = Text; nud.BorderStyle = BorderStyle.FixedSingle;
                    // WinForms NumericUpDown has an internal TextBox that ignores the parent BackColor on value change
                    foreach (Control child in nud.Controls)
                    { child.BackColor = Input; child.ForeColor = Text; }
                    break;
                case CheckBox chk:
                    chk.ForeColor = Text; chk.BackColor = Color.Transparent;
                    break;
                case RadioButton rb:
                    rb.ForeColor = Text; rb.BackColor = Color.Transparent;
                    break;
                case GroupBox gb:
                    gb.ForeColor = Accent; gb.BackColor = Color.Transparent;
                    break;
                case Button:
                    break; // styled by MakeButton
                case Label lbl:
                    lbl.BackColor = Color.Transparent;
                    if (lbl.ForeColor.ToArgb() == Color.Black.ToArgb() ||
                        lbl.ForeColor == SystemColors.ControlText)
                        lbl.ForeColor = Text;
                    break;
                case TabControl tc:
                    tc.BackColor = Bg;
                    break;
                case TabPage tp:
                    tp.BackColor = Bg; tp.ForeColor = Text;
                    break;
                case Panel p:
                    // Only repaint panels that still have the default system color
                    if (p.BackColor == SystemColors.Control) p.BackColor = Bg;
                    break;
            }

            if (c.HasChildren) Apply(c);
        }
    }

    /// <summary>Owner-draws the tab strip with dark tabs and a cyan selected accent.</summary>
    public static void StyleTabs(TabControl tabs)
    {
        tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        tabs.SizeMode = TabSizeMode.Fixed;
        tabs.ItemSize = new Size(108, 30);
        tabs.BackColor = Bg;

        tabs.DrawItem += (s, e) =>
        {
            var tc = (TabControl)s!;
            if (e.Index < 0 || e.Index >= tc.TabPages.Count) return;
            var page = tc.TabPages[e.Index];
            bool selected = e.Index == tc.SelectedIndex;
            var r = tc.GetTabRect(e.Index);

            using var bg = new SolidBrush(selected ? Panel : Bg);
            e.Graphics.FillRectangle(bg, r);

            if (selected)
            {
                using var acc = new SolidBrush(Accent);
                e.Graphics.FillRectangle(acc, r.X, r.Bottom - 3, r.Width, 3);
            }

            TextRenderer.DrawText(e.Graphics, page.Text, tc.Font, r,
                selected ? Accent : TextDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
    }

    /// <summary>Paints a left-to-right gradient with a neon accent underline.</summary>
    public static void PaintHeader(Panel header)
    {
        header.Paint += (s, e) =>
        {
            var rect = header.ClientRectangle;
            using (var brush = new LinearGradientBrush(rect,
                Color.FromArgb(10, 16, 28), Accent2, LinearGradientMode.Horizontal))
            {
                var blend = new ColorBlend
                {
                    Colors = [Color.FromArgb(10, 14, 22), Color.FromArgb(16, 30, 56), Color.FromArgb(20, 60, 90)],
                    Positions = [0f, 0.6f, 1f],
                };
                brush.InterpolationColors = blend;
                e.Graphics.FillRectangle(brush, rect);
            }
            // Neon underline
            using var pen = new Pen(Accent, 2);
            e.Graphics.DrawLine(pen, rect.Left, rect.Bottom - 1, rect.Right, rect.Bottom - 1);
        };
    }
}
