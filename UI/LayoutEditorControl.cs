using System.Drawing.Drawing2D;

namespace LedImageUpdaterService.UI;

/// <summary>
/// Visual drag-and-drop editor for the LED board layout. Draws the canvas
/// (optionally with the rendered preview image as background) and lets the user
/// drag/resize blocks (logo, headers, columns). Writes geometry back to AppConfig.
/// </summary>
internal sealed class LayoutEditorControl : Panel
{
    private AppConfig _cfg = new();
    private Image? _background;
    private readonly List<EditBlock> _blocks = new();

    private EditBlock? _active;
    private bool _resizing;
    private Point _dragStartScreen;
    private Rectangle _dragStartRect;

    private float _scale = 1f;
    private int _offX, _offY;

    private const int HandleSize = 8;

    public event EventHandler? GeometryChanged;

    public LayoutEditorControl()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(30, 30, 30);
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    public void Bind(AppConfig cfg)
    {
        _cfg = cfg;
        Invalidate();
    }

    public void SetBackground(Image? img)
    {
        _background?.Dispose();
        _background = img;
        Invalidate();
    }

    // ─── Block model ──────────────────────────────────────────────────────────

    private sealed class EditBlock
    {
        public required string Label;
        public required Func<Rectangle> Get;
        public required Action<Rectangle> Set;
        public bool ResizeW;
        public bool ResizeH;
        public bool MoveX = true;
        public bool MoveY = true;
        public Color Color;
    }

    private void RebuildBlocks()
    {
        _blocks.Clear();

        _blocks.Add(new EditBlock
        {
            Label = "Лого",
            Color = Color.FromArgb(120, 90, 200),
            ResizeW = true,
            ResizeH = true,
            Get = () => new Rectangle(_cfg.LogoX, _cfg.LogoY, _cfg.LogoW, _cfg.LogoH),
            Set = r => { _cfg.LogoX = r.X; _cfg.LogoY = r.Y; _cfg.LogoW = Math.Max(2, r.Width); _cfg.LogoH = Math.Max(2, r.Height); },
        });

        int hdrW = Math.Max(14, _cfg.FszHdr * 4);
        int hdrH = Math.Max(8, _cfg.FszHdr + 3);

        _blocks.Add(new EditBlock
        {
            Label = "Купля",
            Color = Color.FromArgb(80, 160, 80),
            Get = () => new Rectangle(_cfg.HeaderBuyX, _cfg.HeaderBuyY, hdrW, hdrH),
            Set = r => { _cfg.HeaderBuyX = r.X; _cfg.HeaderBuyY = r.Y; },
        });

        _blocks.Add(new EditBlock
        {
            Label = "Прод",
            Color = Color.FromArgb(180, 120, 60),
            Get = () => new Rectangle(_cfg.HeaderSellX, _cfg.HeaderSellY, hdrW, hdrH),
            Set = r => { _cfg.HeaderSellX = r.X; _cfg.HeaderSellY = r.Y; },
        });

        // Columns shown in the first row band
        _blocks.Add(new EditBlock
        {
            Label = "Флаг",
            Color = Color.FromArgb(70, 130, 180),
            ResizeW = true,
            ResizeH = true,
            Get = () => new Rectangle(_cfg.ColFlagX, _cfg.RowsStartY, _cfg.ColFlagW, _cfg.ColFlagH),
            Set = r => { _cfg.ColFlagX = r.X; _cfg.RowsStartY = r.Y; _cfg.ColFlagW = Math.Max(2, r.Width); _cfg.ColFlagH = Math.Max(2, r.Height); },
        });

        _blocks.Add(new EditBlock
        {
            Label = "Код",
            Color = Color.FromArgb(150, 150, 70),
            MoveY = false,
            Get = () => new Rectangle(_cfg.ColCodeX, _cfg.RowsStartY, Math.Max(18, _cfg.ColBuyX - _cfg.ColCodeX), _cfg.RowH),
            Set = r => { _cfg.ColCodeX = r.X; },
        });

        _blocks.Add(new EditBlock
        {
            Label = "Курс ↓",
            Color = Color.FromArgb(70, 150, 150),
            ResizeW = true,
            MoveY = false,
            Get = () => new Rectangle(_cfg.ColBuyX, _cfg.RowsStartY, _cfg.ColBuyW, _cfg.RowH),
            Set = r => { _cfg.ColBuyX = r.X; _cfg.ColBuyW = Math.Max(4, r.Width); },
        });

        _blocks.Add(new EditBlock
        {
            Label = "Курс ↑",
            Color = Color.FromArgb(170, 90, 110),
            ResizeW = true,
            MoveY = false,
            Get = () => new Rectangle(_cfg.ColSellX, _cfg.RowsStartY, _cfg.ColSellW, _cfg.RowH),
            Set = r => { _cfg.ColSellX = r.X; _cfg.ColSellW = Math.Max(4, r.Width); },
        });
    }

    // ─── Coordinate transforms ────────────────────────────────────────────────

    private void RecomputeScale()
    {
        int cw = Math.Max(1, _cfg.CanvasWidth);
        int ch = Math.Max(1, _cfg.CanvasHeight);
        const int pad = 24;
        float sx = (float)(Width - pad * 2) / cw;
        float sy = (float)(Height - pad * 2) / ch;
        _scale = Math.Max(0.5f, Math.Min(sx, sy));
        _offX = (Width - (int)(cw * _scale)) / 2;
        _offY = (Height - (int)(ch * _scale)) / 2;
    }

    private Rectangle ToScreen(Rectangle r) => new(
        _offX + (int)(r.X * _scale),
        _offY + (int)(r.Y * _scale),
        (int)(r.Width * _scale),
        (int)(r.Height * _scale));

    private Point ToCanvas(Point screen) => new(
        (int)Math.Round((screen.X - _offX) / _scale),
        (int)Math.Round((screen.Y - _offY) / _scale));

    // ─── Painting ─────────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        RecomputeScale();
        RebuildBlocks();

        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        int cw = _cfg.CanvasWidth, ch = _cfg.CanvasHeight;
        var canvasRect = new Rectangle(_offX, _offY, (int)(cw * _scale), (int)(ch * _scale));

        // Canvas background
        using (var bg = new SolidBrush(Color.Black))
            g.FillRectangle(bg, canvasRect);

        if (_background != null)
            g.DrawImage(_background, canvasRect);

        // Canvas border
        using (var pen = new Pen(Color.FromArgb(90, 90, 90)))
            g.DrawRectangle(pen, canvasRect);

        // Faint vertical column dividers
        int colCount = Math.Clamp(_cfg.ColumnCount, 1, 3);
        if (colCount > 1)
        {
            int pitch = cw / colCount;
            using var colPen = new Pen(Color.FromArgb(80, 120, 200, 255)) { DashStyle = DashStyle.Dash };
            for (int i = 1; i < colCount; i++)
            {
                var p = ToScreen(new Rectangle(i * pitch, 0, 1, ch));
                g.DrawLine(colPen, p.X, canvasRect.Top, p.X, canvasRect.Bottom);
            }
        }

        // Faint repeated row guides
        int rows = Math.Max(RowCount(), 1);
        using (var rowPen = new Pen(Color.FromArgb(40, 255, 255, 255)))
        {
            for (int i = 1; i < rows; i++)
            {
                int y = _cfg.RowsStartY + i * _cfg.RowH;
                var p = ToScreen(new Rectangle(0, y, cw, 1));
                g.DrawLine(rowPen, canvasRect.Left, p.Y, canvasRect.Right, p.Y);
            }
        }

        // Blocks
        using var labelFont = new Font("Segoe UI", 7f);
        foreach (var b in _blocks)
        {
            var sr = ToScreen(b.Get());
            using var fill = new SolidBrush(Color.FromArgb(_background != null ? 70 : 130, b.Color));
            using var pen = new Pen(b.Color, b == _active ? 2f : 1f);
            g.FillRectangle(fill, sr);
            g.DrawRectangle(pen, sr);

            // Label
            using var textBrush = new SolidBrush(Color.White);
            using var shadow = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
            var tsz = g.MeasureString(b.Label, labelFont);
            if (tsz.Width + 2 < sr.Width && tsz.Height < sr.Height)
            {
                g.FillRectangle(shadow, sr.X + 1, sr.Y + 1, tsz.Width, tsz.Height);
                g.DrawString(b.Label, labelFont, textBrush, sr.X + 1, sr.Y + 1);
            }

            // Resize handle
            if ((b.ResizeW || b.ResizeH) && b == _active)
            {
                var h = HandleRect(sr);
                using var hb = new SolidBrush(Color.White);
                g.FillRectangle(hb, h);
            }
        }

        // Size caption
        using var capFont = new Font("Segoe UI", 8f);
        using var capBrush = new SolidBrush(Color.Gray);
        g.DrawString($"{cw} × {ch} px   (масштаб {_scale:0.0}×)", capFont, capBrush, 4, 4);
    }

    private static Rectangle HandleRect(Rectangle screenRect) =>
        new(screenRect.Right - HandleSize, screenRect.Bottom - HandleSize, HandleSize, HandleSize);

    // ─── Mouse interaction ────────────────────────────────────────────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        // Topmost block first (last drawn = last in list)
        for (int i = _blocks.Count - 1; i >= 0; i--)
        {
            var b = _blocks[i];
            var sr = ToScreen(b.Get());

            if ((b.ResizeW || b.ResizeH) && HandleRect(sr).Contains(e.Location))
            {
                _active = b;
                _resizing = true;
                _dragStartScreen = e.Location;
                _dragStartRect = b.Get();
                Invalidate();
                return;
            }
            if (sr.Contains(e.Location))
            {
                _active = b;
                _resizing = false;
                _dragStartScreen = e.Location;
                _dragStartRect = b.Get();
                Invalidate();
                return;
            }
        }
        _active = null;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_active == null)
        {
            // Hover cursor feedback
            Cursor = HitAny(e.Location, out bool onHandle)
                ? (onHandle ? Cursors.SizeNWSE : Cursors.SizeAll)
                : Cursors.Default;
            return;
        }

        if (e.Button != MouseButtons.Left) return;

        int dx = (int)Math.Round((e.X - _dragStartScreen.X) / _scale);
        int dy = (int)Math.Round((e.Y - _dragStartScreen.Y) / _scale);

        var r = _dragStartRect;
        if (_resizing)
        {
            int nw = _active.ResizeW ? Math.Max(2, r.Width + dx) : r.Width;
            int nh = _active.ResizeH ? Math.Max(2, r.Height + dy) : r.Height;
            r = new Rectangle(r.X, r.Y, nw, nh);
        }
        else
        {
            int nx = _active.MoveX ? r.X + dx : r.X;
            int ny = _active.MoveY ? r.Y + dy : r.Y;
            // Clamp inside canvas
            nx = Math.Clamp(nx, 0, Math.Max(0, _cfg.CanvasWidth - 2));
            ny = Math.Clamp(ny, 0, Math.Max(0, _cfg.CanvasHeight - 2));
            r = new Rectangle(nx, ny, r.Width, r.Height);
        }

        _active.Set(r);
        Invalidate();
        GeometryChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_active != null)
        {
            _active = null;
            _resizing = false;
            Invalidate();
            GeometryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool HitAny(Point p, out bool onHandle)
    {
        onHandle = false;
        for (int i = _blocks.Count - 1; i >= 0; i--)
        {
            var sr = ToScreen(_blocks[i].Get());
            if ((_blocks[i].ResizeW || _blocks[i].ResizeH) && HandleRect(sr).Contains(p))
            {
                onHandle = true;
                return true;
            }
            if (sr.Contains(p)) return true;
        }
        return false;
    }

    // ─── Auto-layout (Раскидка) ───────────────────────────────────────────────

    /// <summary>
    /// Computes sensible block positions for the current canvas size and currency
    /// count. Single-column layout: logo on top, header row, then evenly spaced rows.
    /// </summary>
    private int RowCount()
        => _cfg.Columns is { Count: > 0 } ? Math.Max(1, _cfg.Columns.Max(c => c.Count)) : 1;

    public void AutoLayout()
    {
        int w = _cfg.CanvasWidth, h = _cfg.CanvasHeight;
        int colCount = Math.Clamp(_cfg.ColumnCount, 1, 3);
        int pitch = w / colCount;               // one column's width
        int rows = RowCount();

        // Logo: top-left, sized to fit a column; smaller when multi-column
        _cfg.LogoX = 2;
        _cfg.LogoY = 2;
        _cfg.LogoW = Math.Max(16, Math.Min(pitch - 4, w / 3));
        _cfg.LogoH = Math.Max(14, (int)(_cfg.LogoW * 0.78));

        int headerY = Math.Max(3, _cfg.LogoH - 8);
        _cfg.HeaderH = Math.Max(10, _cfg.LogoH);

        // Rows fill the area below the logo
        int rowsTop = _cfg.LogoY + _cfg.LogoH + 3;
        int avail = Math.Max(rows * 8, h - rowsTop - 2);
        _cfg.RowsStartY = rowsTop;
        _cfg.RowH = Math.Max(8, avail / rows);

        // Within-column columns: flag | code | buy | sell (relative to pitch)
        int flagW = Math.Max(8, (int)(pitch * 0.18));
        _cfg.ColFlagX = 2;
        _cfg.ColFlagW = flagW;
        _cfg.ColFlagH = Math.Max(8, (int)(_cfg.RowH * 0.8));

        int codeX = _cfg.ColFlagX + flagW + 2;
        _cfg.ColCodeX = codeX;

        int remaining = pitch - codeX - 2;
        int codeSpace = Math.Max(14, (int)(remaining * 0.28));
        int buyX = codeX + codeSpace;
        int colW = Math.Max(8, (pitch - buyX - 2) / 2);
        _cfg.ColBuyX = buyX;
        _cfg.ColBuyW = colW;
        _cfg.ColSellX = buyX + colW;
        _cfg.ColSellW = colW;

        // Headers above buy/sell (within column)
        _cfg.HeaderBuyX = _cfg.ColBuyX + colW / 2;
        _cfg.HeaderBuyY = headerY;
        _cfg.HeaderSellX = _cfg.ColSellX + colW / 2;
        _cfg.HeaderSellY = headerY;

        Invalidate();
        GeometryChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _background?.Dispose();
        base.Dispose(disposing);
    }
}
