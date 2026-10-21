using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System.Globalization;
using System.Text.Json.Nodes;
namespace Nexolume.Desktop.Editors.Sheets.Controls;

public readonly record struct CellRange(int Left, int Top, int Right, int Bottom)
{
    public long Count => (long)(Right - Left + 1) * (Bottom - Top + 1);
    public bool Contains(int col, int row) => col >= Left && col <= Right && row >= Top && row <= Bottom;
}

/// <summary>Conditional format applied to the cells of <see cref="Range"/> whose value satisfies <see cref="Op"/>.</summary>
public sealed record GridRule(CellRange Range, string Op, string Value, string Value2, string Fill, string Color)
{
    public bool Matches(JsonNode? cell)
    {
        var display = cell?["display"]?.GetValue<string>() ?? "";
        if (Op == "empty") return display.Length == 0;
        if (Op == "contains") return Value.Length > 0 && display.Contains(Value, StringComparison.CurrentCultureIgnoreCase);
        if (!TryNumber(cell, out var n)) return false;
        return Op switch
        {
            "gt" => TryParse(Value, out var a) && n > a,
            "lt" => TryParse(Value, out var b) && n < b,
            "eq" => TryParse(Value, out var c) && Math.Abs(n - c) < 1e-9,
            "between" => TryParse(Value, out var lo) && TryParse(Value2, out var hi) && n >= Math.Min(lo, hi) && n <= Math.Max(lo, hi),
            "neg" => n < 0,
            _ => false,
        };
    }
    public static bool TryParse(string text, out double value) => double.TryParse(text.Replace(',', '.').Replace(" ", "").Replace(" ", "").TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    public static bool TryNumber(JsonNode? cell, out double value)
    {
        value = 0; var input = cell?["input"]?.GetValue<string>() ?? "";
        return !input.StartsWith('=') && TryParse(input, out value) || input.StartsWith('=') && TryParse(cell?["display"]?.GetValue<string>() ?? "", out value);
    }
}

public sealed class SheetGrid : Control
{
    public JsonObject Cells { get; set; } = new();
    public int Column { get; private set; }
    public int Row { get; private set; }
    private int anchorColumn, anchorRow;
    private bool dragging;
    private int resizing = -1; private bool resizingColumn; private double resizeStart, resizeOrigin;
    public int FirstRow { get; set; }
    public int FirstColumn { get; set; }
    public double Zoom { get; set; } = 1;
    public bool ShowFormulas { get; set; }
    public bool ShowGridLines { get; set; } = true;
    public Dictionary<int, double> ColumnWidths { get; } = new();
    public Dictionary<int, double> RowHeights { get; } = new();
    public int FrozenRows { get; set; }
    public int FrozenColumns { get; set; }
    public List<CellRange> Merges { get; } = new();
    public List<GridRule> Rules { get; } = new();
    public CellRange Selection => new(Math.Min(anchorColumn, Column), Math.Min(anchorRow, Row), Math.Max(anchorColumn, Column), Math.Max(anchorRow, Row));
    public string Address => CellName(Column, Row);
    public string SelectionAddress => Selection.Count == 1 ? Address : $"{CellName(Selection.Left, Selection.Top)}:{CellName(Selection.Right, Selection.Bottom)}";
    public event Action? SelectionChanged;
    public event Action? ViewportChanged;
    public event Action<string?>? EditRequested;
    public event Action? ClearRequested;
    public event Action<int, double>? ColumnResized;
    public event Action<int, double>? RowResized;
    private const double CellWidth = 116, CellHeight = 32, HeaderWidth = 54, HeaderHeight = 34;
    public SheetGrid() { Focusable = true; ClipToBounds = true; }

    private double Size(bool columns, int index) => (columns ? ColumnWidths : RowHeights).TryGetValue(index, out var s) ? s : columns ? CellWidth : CellHeight;
    private int Frozen(bool columns) => columns ? FrozenColumns : FrozenRows;
    private int First(bool columns) => Math.Max(columns ? FirstColumn : FirstRow, Frozen(columns));
    // Position of an index on one axis, before the header: frozen lines stay put, the rest follows the scroll offset.
    private double Offset(bool columns, int index)
    {
        int frozen = Frozen(columns), first = First(columns); double pos = 0;
        if (index < frozen) { for (int i = 0; i < index; i++) pos += Size(columns, i); return pos; }
        for (int i = 0; i < frozen; i++) pos += Size(columns, i);
        if (index >= first) { if (index - first > 2000) return pos + (index - first) * Size(columns, first); for (int i = first; i < index; i++) pos += Size(columns, i); }
        else for (int i = index; i < first; i++) pos -= Size(columns, i);
        return pos;
    }
    private List<(int Index, double Pos, double Size)> Slots(bool columns, double extent)
    {
        var list = new List<(int, double, double)>(); int frozen = Frozen(columns), max = columns ? 16384 : 1048576; double pos = 0;
        for (int i = 0; i < frozen && i < max; i++) { var s = Size(columns, i); list.Add((i, pos, s)); pos += s; }
        for (int i = First(columns); i < max && pos < extent; i++) { var s = Size(columns, i); list.Add((i, pos, s)); pos += s; }
        return list;
    }
    private int IndexAt(bool columns, double pos, double extent)
    {
        var slots = Slots(columns, extent); if (slots.Count == 0) return 0;
        foreach (var s in slots) if (pos >= s.Pos && pos < s.Pos + s.Size) return s.Index;
        var last = slots[^1]; return pos < 0 ? slots[0].Index : last.Index + 1 + (int)((pos - last.Pos - last.Size) / (columns ? CellWidth : CellHeight));
    }
    public CellRange? MergeAt(int col, int row) { foreach (var m in Merges) if (m.Contains(col, row)) return m; return null; }
    private Rect CellRect(int col, int row)
    {
        var m = MergeAt(col, row) ?? new CellRange(col, row, col, row);
        double x = Offset(true, m.Left), y = Offset(false, m.Top);
        return new Rect(HeaderWidth + x, HeaderHeight + y, Offset(true, m.Right) + Size(true, m.Right) - x, Offset(false, m.Bottom) + Size(false, m.Bottom) - y);
    }
    public Rect? ActiveCellRect()
    {
        var r = CellRect(Column, Row); var z = new Rect(r.X * Zoom, r.Y * Zoom, r.Width * Zoom, r.Height * Zoom);
        if (z.Right <= HeaderWidth * Zoom || z.Bottom <= HeaderHeight * Zoom || z.X >= Bounds.Width || z.Y >= Bounds.Height) return null;
        return z;
    }
    public static (int Column, int Row) ParseAddress(string address) { int i = 0, column = 0; while (i < address.Length && char.IsLetter(address[i])) column = column * 26 + (char.ToUpperInvariant(address[i++]) - 'A' + 1); return (column - 1, int.Parse(address[i..]) - 1); }
    public static string CellName(int column, int row) { var name = ""; for (int n = column + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name; return name + (row + 1); }
    public static CellRange ParseRange(string range) { var parts = range.Split(':'); var a = ParseAddress(parts[0]); var b = parts.Length > 1 ? ParseAddress(parts[1]) : a; return new(Math.Min(a.Column, b.Column), Math.Min(a.Row, b.Row), Math.Max(a.Column, b.Column), Math.Max(a.Row, b.Row)); }
    public static string RangeName(CellRange r) => $"{CellName(r.Left, r.Top)}:{CellName(r.Right, r.Bottom)}";
    public void SelectRange(int left, int top, int right, int bottom) { Select(left, top); Select(right, bottom, true); }
    public void Select(int column, int row, bool extend = false)
    {
        column = Math.Clamp(column, 0, 16383); row = Math.Clamp(row, 0, 1048575);
        if (!extend && MergeAt(column, row) is { } merge) { column = merge.Left; row = merge.Top; }
        Column = column; Row = row;
        if (!extend) { anchorColumn = Column; anchorRow = Row; }
        EnsureVisible(true, Column); EnsureVisible(false, Row);
        InvalidateVisual(); ViewportChanged?.Invoke(); SelectionChanged?.Invoke();
    }
    private void EnsureVisible(bool columns, int index)
    {
        if (index < Frozen(columns)) return;
        double avail = (columns ? Bounds.Width : Bounds.Height) / Zoom - (columns ? HeaderWidth : HeaderHeight);
        int first = columns ? FirstColumn : FirstRow;
        if (index < first) first = index;
        else
        {
            if (index - first > 100) first = Math.Max(Frozen(columns), index - 5);
            int guard = 0; while (first < index && guard++ < 200) { FirstSet(columns, first); if (Offset(columns, index) + Size(columns, index) <= avail) break; first++; }
        }
        FirstSet(columns, first);
    }
    private void FirstSet(bool columns, int value) { if (columns) FirstColumn = value; else FirstRow = value; }
    private (int Col, int Row) Hit(Point p) => (IndexAt(true, p.X / Zoom - HeaderWidth, Bounds.Width / Zoom), IndexAt(false, p.Y / Zoom - HeaderHeight, Bounds.Height / Zoom));
    private int Border(bool columns, Point p)
    {
        double along = (columns ? p.X : p.Y) / Zoom - (columns ? HeaderWidth : HeaderHeight), across = columns ? p.Y / Zoom : p.X / Zoom;
        if (across >= (columns ? HeaderHeight : HeaderWidth) || along < -4) return -1;
        foreach (var s in Slots(columns, (columns ? Bounds.Width : Bounds.Height) / Zoom)) if (Math.Abs(along - (s.Pos + s.Size)) <= 4) return s.Index;
        return -1;
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); var p = e.GetPosition(this);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            int column = Border(true, p), row = column < 0 ? Border(false, p) : -1;
            if (column >= 0 || row >= 0) { resizingColumn = column >= 0; resizing = resizingColumn ? column : row; resizeStart = resizingColumn ? p.X : p.Y; resizeOrigin = Size(resizingColumn, resizing); e.Pointer.Capture(this); e.Handled = true; return; }
        }
        if (p.X < HeaderWidth * Zoom || p.Y < HeaderHeight * Zoom) return;
        Focus(); var cell = Hit(p); var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed) { if (!Selection.Contains(cell.Col, cell.Row)) Select(cell.Col, cell.Row); return; }
        if (!point.Properties.IsLeftButtonPressed) return;
        Select(cell.Col, cell.Row, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        if (e.ClickCount == 2) EditRequested?.Invoke(null); else { dragging = true; e.Pointer.Capture(this); }
        e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); var p = e.GetPosition(this);
        if (resizing >= 0)
        {
            var size = Math.Clamp(resizeOrigin + ((resizingColumn ? p.X : p.Y) - resizeStart) / Zoom, 20, 800); (resizingColumn ? ColumnWidths : RowHeights)[resizing] = Math.Round(size);
            InvalidateVisual(); ViewportChanged?.Invoke(); return;
        }
        if (!dragging) { Cursor = Border(true, p) >= 0 ? new Cursor(StandardCursorType.SizeWestEast) : Border(false, p) >= 0 ? new Cursor(StandardCursorType.SizeNorthSouth) : null; return; }
        var cell = Hit(new Point(Math.Max(HeaderWidth * Zoom, p.X), Math.Max(HeaderHeight * Zoom, p.Y)));
        if (cell.Col != Column || cell.Row != Row) Select(cell.Col, cell.Row, true);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e); dragging = false; e.Pointer.Capture(null);
        if (resizing >= 0) { var index = resizing; var column = resizingColumn; resizing = -1; var size = Size(column, index); if (column) ColumnResized?.Invoke(index, size); else RowResized?.Invoke(index, size); }
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); dragging = false; resizing = -1; }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e); if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) FirstColumn = Math.Clamp(FirstColumn - (int)e.Delta.Y * 3, 0, 16360); else FirstRow = Math.Clamp(FirstRow - (int)e.Delta.Y * 3, 0, 1048550);
        InvalidateVisual(); ViewportChanged?.Invoke(); e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        var extend = e.KeyModifiers.HasFlag(KeyModifiers.Shift); var span = MergeAt(Column, Row);
        switch (e.Key)
        {
            case Key.Left: Select(Column - 1, Row, extend); break;
            case Key.Right: Select((span?.Right ?? Column) + 1, Row, extend); break;
            case Key.Tab: Select(extend ? Column - 1 : (span?.Right ?? Column) + 1, Row); break;
            case Key.Up: Select(Column, Row - 1, extend); break;
            case Key.Down: Select(Column, (span?.Bottom ?? Row) + 1, extend); break;
            case Key.Enter: case Key.F2: EditRequested?.Invoke(null); break;
            case Key.Delete: ClearRequested?.Invoke(); break;
            case Key.Back: EditRequested?.Invoke(""); break;
            case Key.PageDown: Select(Column, Row + 20, extend); break;
            case Key.PageUp: Select(Column, Row - 20, extend); break;
            case Key.Escape: Select(Column, Row); break;
            default: return;
        }
        e.Handled = true;
    }
    protected override void OnTextInput(TextInputEventArgs e) { base.OnTextInput(e); if (!string.IsNullOrEmpty(e.Text) && !char.IsControl(e.Text[0])) { EditRequested?.Invoke(e.Text); e.Handled = true; } }
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.White, new Rect(Bounds.Size));
        using var scale = context.PushTransform(Matrix.CreateScale(Zoom, Zoom));
        var width = Bounds.Width / Zoom; var height = Bounds.Height / Zoom;
        var line = new Pen(Brush.Parse("#E1E7EA"), 1); var ink = Brush.Parse("#23382F"); var accent = Brush.Parse("#287861"); var range = Selection;
        var cols = Slots(true, width - HeaderWidth); var rows = Slots(false, height - HeaderHeight);
        if (range.Count == 1 && MergeAt(range.Left, range.Top) is { } wholeMerge) range = wholeMerge;
        FormattedText Label(string text, IBrush? color = null, bool bold = false, bool italic = false) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI", italic ? FontStyle.Italic : FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), 13, color ?? ink);
        void DrawCell(int col, int row, Rect rect)
        {
            var cell = Cells[CellName(col, row)]; var st = cell?["style"];
            var fill = st?["fill"]?.GetValue<string>(); var textColor = st?["color"]?.GetValue<string>();
            foreach (var rule in Rules) if (rule.Range.Contains(col, row) && rule.Matches(cell)) { if (rule.Fill.Length == 7) fill = rule.Fill; if (rule.Color.Length == 7) textColor = rule.Color; }
            if (fill is { Length: 7 } && fill[0] == '#') context.FillRectangle(Brush.Parse(fill), rect);
            if (range.Contains(col, row)) context.FillRectangle(new SolidColorBrush(Color.Parse("#E5F2ED"), fill is { Length: 7 } ? 0.55 : 1), rect);
            var value = cell?[ShowFormulas ? "input" : "display"]?.GetValue<string>();
            if (value is not null)
            {
                using var clip = context.PushClip(rect.Deflate(3));
                var number = !ShowFormulas && (cell?["input"]?.GetValue<string>().StartsWith('=') == true || double.TryParse(cell?["input"]?.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out _));
                IBrush? brush = value.StartsWith('#') ? Brushes.Firebrick : textColor is { Length: 7 } && textColor[0] == '#' ? Brush.Parse(textColor) : null;
                var label = Label(value, brush, st?["bold"]?.GetValue<bool>() ?? false, st?["italic"]?.GetValue<bool>() ?? false);
                var align = st?["align"]?.GetValue<string>() ?? "general"; var top = rect.Y + Math.Max(2, (rect.Height - 18) / 2);
                var x = align switch { "left" => rect.X + 9, "center" => rect.X + (rect.Width - label.Width) / 2, "right" => Math.Max(rect.X + 8, rect.Right - label.Width - 9), _ => number ? Math.Max(rect.X + 8, rect.Right - label.Width - 9) : rect.X + 9 };
                context.DrawText(label, new Point(x, top));
                if (st?["underline"]?.GetValue<bool>() == true) context.DrawLine(new Pen(brush ?? ink, 1), new Point(x, top + 17), new Point(x + label.Width, top + 17));
            }
            if (ShowGridLines) context.DrawRectangle(null, line, rect);
        }
        foreach (var r in rows) foreach (var c in cols)
        {
            if (Merges.Count > 0 && MergeAt(c.Index, r.Index) is not null) continue;
            DrawCell(c.Index, r.Index, new Rect(HeaderWidth + c.Pos, HeaderHeight + r.Pos, c.Size, r.Size));
        }
        if (Merges.Count > 0)
        {
            int firstCol = cols.Count > 0 ? cols[0].Index : 0, lastCol = cols.Count > 0 ? cols[^1].Index : 0, firstRow = rows.Count > 0 ? rows[0].Index : 0, lastRow = rows.Count > 0 ? rows[^1].Index : 0;
            foreach (var m in Merges)
            {
                bool colVisible = m.Left < FrozenColumns || (m.Right >= First(true) && m.Left <= lastCol), rowVisible = m.Top < FrozenRows || (m.Bottom >= First(false) && m.Top <= lastRow);
                if (colVisible && rowVisible) DrawCell(m.Left, m.Top, CellRect(m.Left, m.Top));
            }
        }
        using (context.PushClip(new Rect(HeaderWidth, HeaderHeight, Math.Max(0, width - HeaderWidth), Math.Max(0, height - HeaderHeight))))
        {
            double sx = Offset(true, range.Left), sy = Offset(false, range.Top);
            var selected = new Rect(HeaderWidth + sx, HeaderHeight + sy, Offset(true, range.Right) + Size(true, range.Right) - sx, Offset(false, range.Bottom) + Size(false, range.Bottom) - sy);
            context.DrawRectangle(null, new Pen(accent, 2), selected.Deflate(1));
            if (range.Count > 1) { var active = CellRect(Column, Row); context.DrawRectangle(null, new Pen(accent, 1), active.Deflate(2)); }
            var frozen = new Pen(Brush.Parse("#6B8A7E"), 2);
            if (FrozenColumns > 0) { var x = HeaderWidth + Offset(true, FrozenColumns); context.DrawLine(frozen, new Point(x, HeaderHeight), new Point(x, height)); }
            if (FrozenRows > 0) { var y = HeaderHeight + Offset(false, FrozenRows); context.DrawLine(frozen, new Point(HeaderWidth, y), new Point(width, y)); }
        }
        var header = Brush.Parse("#F2F6F4"); context.FillRectangle(header, new Rect(0, 0, width, HeaderHeight)); context.FillRectangle(header, new Rect(0, 0, HeaderWidth, height));
        foreach (var c in cols)
        {
            var x = HeaderWidth + c.Pos; bool selected = c.Index >= range.Left && c.Index <= range.Right;
            if (selected) context.FillRectangle(Brush.Parse("#D6EAE1"), new Rect(x, 0, c.Size, HeaderHeight));
            var label = Label(CellName(c.Index, 0)[..^1], selected ? accent : ink, selected);
            context.DrawText(label, new Point(x + (c.Size - label.Width) / 2, 9)); context.DrawRectangle(null, line, new Rect(x, 0, c.Size, HeaderHeight));
        }
        foreach (var r in rows)
        {
            var y = HeaderHeight + r.Pos; bool selected = r.Index >= range.Top && r.Index <= range.Bottom;
            if (selected) context.FillRectangle(Brush.Parse("#D6EAE1"), new Rect(0, y, HeaderWidth, r.Size));
            var label = Label((r.Index + 1).ToString(), selected ? accent : ink, selected);
            context.DrawText(label, new Point((HeaderWidth - label.Width) / 2, y + Math.Max(2, (r.Size - 18) / 2))); context.DrawRectangle(null, line, new Rect(0, y, HeaderWidth, r.Size));
        }
        context.FillRectangle(header, new Rect(0, 0, HeaderWidth, HeaderHeight)); context.DrawRectangle(null, line, new Rect(0, 0, HeaderWidth, HeaderHeight));
    }
}
