using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;

namespace LedImageUpdaterService.UI;

/// <summary>
/// Dark "futuristic" theme: deep slate backgrounds with a neon cyan accent.
/// Applied recursively to the settings form so individual builders stay simple.
///
/// The look targets a modern web UI rather than classic WinForms chrome: inputs are
/// borderless with a rounded frame painted by their parent (so focus can show an accent
/// ring), group boxes render as cards, and tabs as a segmented control. Nothing here
/// changes control sizes or positions — only painting — so existing layouts stay intact.
/// </summary>
internal static class UITheme
{
    public static readonly Color Bg = Color.FromArgb(14, 18, 24);        // form background
    public static readonly Color Panel = Color.FromArgb(20, 26, 34);     // panels / footer
    public static readonly Color Card = Color.FromArgb(23, 30, 39);      // group box surface
    public static readonly Color Input = Color.FromArgb(27, 34, 44);     // text/list/combo
    public static readonly Color Border = Color.FromArgb(40, 50, 62);
    public static readonly Color Accent = Color.FromArgb(0, 229, 200);   // neon cyan/teal
    public static readonly Color Accent2 = Color.FromArgb(31, 111, 235); // electric blue
    public static readonly Color Text = Color.FromArgb(214, 222, 231);
    public static readonly Color TextDim = Color.FromArgb(140, 150, 165);

    /// <summary>Corner radius shared by inputs, cards and tab chips.</summary>
    public const int Radius = 8;

    // Inputs whose rounded frame is painted by their parent.
    private static readonly HashSet<Control> Inputs = [];
    // Parents that already carry the frame-painting handler.
    private static readonly HashSet<Control> FramedParents = [];

    // ─── Color helpers ───────────────────────────────────────────────────────

    public static Color Lighten(Color c, double a) => Color.FromArgb(c.A,
        (int)Math.Min(255, c.R + (255 - c.R) * a),
        (int)Math.Min(255, c.G + (255 - c.G) * a),
        (int)Math.Min(255, c.B + (255 - c.B) * a));

    public static Color Darken(Color c, double a) => Color.FromArgb(c.A,
        (int)Math.Max(0, c.R * (1 - a)),
        (int)Math.Max(0, c.G * (1 - a)),
        (int)Math.Max(0, c.B * (1 - a)));

    /// <summary>Blends <paramref name="a"/> toward <paramref name="b"/> by <paramref name="t"/> (0..1).</summary>
    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(255,
        (int)(a.R + (b.R - a.R) * t),
        (int)(a.G + (b.G - a.G) * t),
        (int)(a.B + (b.B - a.B) * t));

    // ─── Input text insets ───────────────────────────────────────────────────

    private const int EM_SETMARGINS = 0x00D3;
    private const int EC_LEFTMARGIN = 0x0001;
    private const int EC_RIGHTMARGIN = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Adds web-like horizontal padding inside a borderless text box.</summary>
    private static void SetTextMargins(TextBox tb, int left, int right)
    {
        void ApplyMargins()
        {
            if (!tb.IsHandleCreated) return;
            try { SendMessage(tb.Handle, EM_SETMARGINS, EC_LEFTMARGIN | EC_RIGHTMARGIN, (right << 16) | left); }
            catch { }
        }
        if (tb.IsHandleCreated) ApplyMargins(); else tb.HandleCreated += (_, _) => ApplyMargins();
    }

    // ─── Theme application ───────────────────────────────────────────────────

    /// <summary>Recursively applies the dark theme to a control tree.</summary>
    public static void Apply(Control root)
    {
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case TextBox tb:
                    tb.BackColor = Input; tb.ForeColor = Text; tb.BorderStyle = BorderStyle.None;
                    SetTextMargins(tb, 6, 6);
                    RegisterInput(tb);
                    break;
                case ListBox lb:
                    lb.BackColor = Input; lb.ForeColor = Text; lb.BorderStyle = BorderStyle.None;
                    RegisterInput(lb);
                    break;
                case ComboBox cb:
                    cb.BackColor = Input; cb.ForeColor = Text; cb.FlatStyle = FlatStyle.Flat;
                    RegisterInput(cb);
                    break;
                case NumericUpDown nud:
                    nud.BackColor = Input; nud.ForeColor = Text; nud.BorderStyle = BorderStyle.None;
                    // WinForms NumericUpDown has an internal TextBox that ignores the parent BackColor on value change
                    foreach (Control child in nud.Controls)
                    { child.BackColor = Input; child.ForeColor = Text; }
                    RegisterInput(nud);
                    break;
                case CheckBox chk:
                    chk.ForeColor = Text; chk.BackColor = Color.Transparent;
                    chk.FlatStyle = FlatStyle.Flat;
                    chk.FlatAppearance.BorderColor = Border;
                    chk.Cursor = Cursors.Hand;
                    break;
                case RadioButton rb:
                    rb.ForeColor = Text; rb.BackColor = Color.Transparent;
                    rb.FlatStyle = FlatStyle.Flat;
                    rb.FlatAppearance.BorderColor = Border;
                    rb.Cursor = Cursors.Hand;
                    break;
                case GroupBox gb:
                    StyleCard(gb);
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

    /// <summary>
    /// Marks a control as an input: its parent paints a rounded frame around it, tinted
    /// with the accent while the input has focus (the web "focus ring").
    /// </summary>
    private static void RegisterInput(Control input)
    {
        if (!Inputs.Add(input)) return;

        void Refresh() => input.Parent?.Invalidate(Rectangle.Inflate(input.Bounds, 6, 6), false);

        input.GotFocus += (_, _) => Refresh();
        input.LostFocus += (_, _) => Refresh();
        input.Enter += (_, _) => Refresh();
        input.Leave += (_, _) => Refresh();
        input.LocationChanged += (_, _) => input.Parent?.Invalidate();
        input.SizeChanged += (_, _) => input.Parent?.Invalidate();
        input.Disposed += (_, _) => Inputs.Remove(input);

        // NumericUpDown/ComboBox route focus to inner children — track those too.
        foreach (Control child in input.Controls)
        {
            child.GotFocus += (_, _) => Refresh();
            child.LostFocus += (_, _) => Refresh();
        }

        AttachFramePainter(input.Parent);
        input.ParentChanged += (_, _) => AttachFramePainter(input.Parent);
    }

    private static void AttachFramePainter(Control? parent)
    {
        if (parent is null || !FramedParents.Add(parent)) return;

        parent.Paint += (s, e) =>
        {
            var host = (Control)s!;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            foreach (Control child in host.Controls)
            {
                if (!Inputs.Contains(child) || !child.Visible) continue;

                bool focused = child.Focused || child.ContainsFocus;
                var r = Rectangle.Inflate(child.Bounds, 3, 3);
                r.Width -= 1; r.Height -= 1;
                if (r.Width <= 0 || r.Height <= 0) continue;

                using var path = RoundedButton.RoundedRect(r, Radius);
                using (var fill = new SolidBrush(child.Enabled ? Input : Panel))
                    g.FillPath(fill, path);
                using (var pen = new Pen(focused ? Accent : Border, focused ? 1.6f : 1f))
                    g.DrawPath(pen, path);

                if (!focused) continue;

                // Outer glow — the WinForms stand-in for a CSS focus box-shadow
                var glow = Rectangle.Inflate(r, 2, 2);
                using var gp = RoundedButton.RoundedRect(glow, Radius + 2);
                using var gpen = new Pen(Color.FromArgb(70, Accent), 2f);
                g.DrawPath(gpen, gp);
            }
        };

        parent.Disposed += (_, _) => FramedParents.Remove(parent);
    }

    /// <summary>Repaints a GroupBox as a flat card: rounded surface, hairline border, accent title.</summary>
    public static void StyleCard(GroupBox gb)
    {
        gb.ForeColor = Accent;
        gb.BackColor = Color.Transparent;
        EnableDoubleBuffering(gb);

        gb.Paint += (s, e) =>
        {
            var box = (GroupBox)s!;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var r = box.ClientRectangle;
            r.Width -= 1; r.Height -= 1;
            int top = string.IsNullOrEmpty(box.Text) ? 0 : 7;
            r.Y += top; r.Height -= top;
            if (r.Width <= 0 || r.Height <= 0) return;

            using (var path = RoundedButton.RoundedRect(r, Radius + 2))
            {
                using var fill = new SolidBrush(Card);
                g.FillPath(fill, path);
                using var pen = new Pen(Border, 1f);
                g.DrawPath(pen, path);
            }

            if (string.IsNullOrEmpty(box.Text)) return;

            // Title sits on the card edge, like a fieldset legend
            var size = TextRenderer.MeasureText(g, box.Text, box.Font);
            var tr = new Rectangle(r.X + 10, r.Y - top, size.Width + 10, size.Height);
            using (var chip = new SolidBrush(Bg))
                g.FillRectangle(chip, tr);
            TextRenderer.DrawText(g, box.Text, box.Font, new Point(tr.X + 5, tr.Y), Accent);
        };
    }

    // GroupBox paints its own frame before the Paint event fires; double buffering keeps
    // that frame from flickering through the card. DoubleBuffered is protected, hence reflection.
    private static void EnableDoubleBuffering(Control c)
    {
        typeof(Control)
            .GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(c, true);
    }

    /// <summary>Owner-draws the tab strip as a segmented control with a rounded active chip.</summary>
    public static void StyleTabs(TabControl tabs)
    {
        tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        tabs.SizeMode = TabSizeMode.Fixed;
        tabs.ItemSize = new Size(112, 34);
        tabs.BackColor = Bg;

        tabs.DrawItem += (s, e) =>
        {
            var tc = (TabControl)s!;
            if (e.Index < 0 || e.Index >= tc.TabPages.Count) return;
            var page = tc.TabPages[e.Index];
            bool selected = e.Index == tc.SelectedIndex;
            var r = tc.GetTabRect(e.Index);
            bool hot = !selected && r.Contains(tc.PointToClient(Cursor.Position));

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var bg = new SolidBrush(Bg))
                g.FillRectangle(bg, r);

            // Rounded chip: filled for the active tab, hinted on hover
            var chip = new Rectangle(r.X + 3, r.Y + 4, r.Width - 6, r.Height - 7);
            if (selected || hot)
            {
                using var path = RoundedButton.RoundedRect(chip, Radius);
                using var fill = new SolidBrush(selected ? Panel : Lighten(Bg, 0.05));
                g.FillPath(fill, path);
                if (selected)
                {
                    using var pen = new Pen(Color.FromArgb(90, Accent), 1f);
                    g.DrawPath(pen, path);
                    // Short accent bar under the label
                    using var bar = new SolidBrush(Accent);
                    g.FillRectangle(bar, chip.X + chip.Width / 2 - 12, chip.Bottom - 3, 24, 2);
                }
            }

            TextRenderer.DrawText(g, page.Text, tc.Font, chip,
                selected ? Accent : hot ? Text : TextDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };

        // Hover feedback needs a repaint as the mouse crosses the strip
        tabs.MouseMove += (_, _) => tabs.Invalidate();
        tabs.MouseLeave += (_, _) => tabs.Invalidate();
    }

    /// <summary>Paints a left-to-right gradient with a neon accent underline.</summary>
    public static void PaintHeader(Panel header)
    {
        header.Paint += (s, e) =>
        {
            var rect = header.ClientRectangle;
            if (rect.Width <= 0 || rect.Height <= 0) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var brush = new LinearGradientBrush(rect,
                Color.FromArgb(10, 16, 28), Accent2, LinearGradientMode.Horizontal))
            {
                var blend = new ColorBlend
                {
                    Colors = [Color.FromArgb(10, 14, 22), Color.FromArgb(16, 30, 56), Color.FromArgb(20, 60, 90)],
                    Positions = [0f, 0.6f, 1f],
                };
                brush.InterpolationColors = blend;
                g.FillRectangle(brush, rect);
            }

            // Soft accent halo toward the right edge, then the neon underline
            using (var halo = new LinearGradientBrush(rect,
                Color.FromArgb(0, Accent), Color.FromArgb(38, Accent), LinearGradientMode.Horizontal))
                g.FillRectangle(halo, rect);

            using var pen = new Pen(Accent, 2);
            g.DrawLine(pen, rect.Left, rect.Bottom - 1, rect.Right, rect.Bottom - 1);
        };
    }
}
