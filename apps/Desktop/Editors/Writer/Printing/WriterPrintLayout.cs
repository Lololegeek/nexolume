using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Skia.Helpers;
using Avalonia.Utilities;
using SkiaSharp;
using System.Text.Json.Nodes;

namespace Nexolume.Desktop.Editors.Writer.Printing;

// A snapshot owns its shaped paragraphs. Preview, PDF and printer consume the same lines.
public sealed class WriterPrintLayout : IDisposable
{
    public sealed record Line(TextLine Text, Point Origin, string ParagraphId);
    public sealed record Marker(TextLayout Text, Point Origin);
    public sealed record Picture(Bitmap Bitmap, Rect Bounds);
    public sealed record Cell(Rect Bounds, bool Header);
    public sealed class Frame(Rect bounds, string fill, string border) { public Rect Bounds { get; set; } = bounds; public string Fill { get; } = fill; public string Border { get; } = border; }
    public sealed class Page
    {
        public List<Line> Lines { get; } = [];
        public List<Marker> Markers { get; } = [];
        public List<Picture> Pictures { get; } = [];
        public List<Cell> Cells { get; } = [];
        public List<Frame> Frames { get; } = [];
        public bool Used => Lines.Count > 0 || Pictures.Count > 0 || Cells.Count > 0;
    }
    public PageSettings Settings { get; }
    public IReadOnlyList<Page> Pages => pages;
    public string Title { get; }
    public string Header { get; set; } = "";
    public string Footer { get; set; } = "";
    public bool NumberPages { get; set; }
    private readonly List<Page> pages = [new()];
    private readonly List<TextLayout> shaped = [];
    private readonly List<Bitmap> bitmaps = [];
    private bool disposed;
    public WriterPrintLayout(JsonObject snapshot)
    {
        Settings = PageSettings.Read(snapshot); Settings.Validate(); Header = Settings.Header; Footer = Settings.Footer; Title = snapshot["title"]!.GetValue<string>();
        var y = Settings.Top; var bottom = Settings.Height - Settings.Bottom; var numbered = 0;
        var columns = Settings.Columns; var gap = Settings.ColumnGap; var columnWidth = (Settings.Width - Settings.Left - Settings.Right - gap * (columns - 1)) / columns; var column = 0;
        double Left() => Settings.Left + column * (columnWidth + gap);
        try
        {
            foreach (var paragraph in snapshot["content"]!["paragraphs"]!.AsArray())
            {
                var p = paragraph!; var style = p["style"]!; var text = p["text"]!.GetValue<string>(); var list = style["list"]!.GetValue<string>();
                numbered = list == "number" ? numbered + 1 : 0;
                if (style["page_break_before"]?.GetValue<bool>() == true && pages[^1].Used) { NewPage(); }
                var indent = style["indent"]!.GetValue<int>() * 24.0;
                var markerWidth = list == "none" ? 0 : 28;
                var width = columnWidth - indent - markerWidth;
                if (width < 24) throw new InvalidOperationException("Le retrait est trop grand pour la largeur de la page.");
                if (p["table"] is JsonObject table)
                {
                    var rows = table["rows"]!.AsArray(); var tableColumns = rows[0]!.AsArray().Count; var header = table["header"]!.GetValue<bool>(); const double pad = 6;
                    var cellWidth = width / tableColumns; if (cellWidth - 2 * pad < 12) throw new InvalidOperationException("Le tableau a trop de colonnes pour la largeur de la page.");
                    for (var r = 0; r < rows.Count; r++)
                    {
                        var head = header && r == 0; var cells = new List<TextLayout>(); var tall = 0.0;
                        for (var c = 0; c < tableColumns; c++)
                        {
                            var cell = new TextLayout(rows[r]![c]!.GetValue<string>(), new Typeface(new FontFamily(style["font_family"]!.GetValue<string>()), FontStyle.Normal, head ? FontWeight.Bold : FontWeight.Normal), style["font_size"]!.GetValue<double>(), Brushes.Black, textWrapping: TextWrapping.Wrap, maxWidth: columnWidth - 2 * pad);
                            shaped.Add(cell); cells.Add(cell); tall = Math.Max(tall, cell.Height);
                        }
                        var rowHeight = tall + 2 * pad; if (rowHeight > bottom - Settings.Top + 0.01) throw new InvalidOperationException("Une ligne de tableau est plus haute que la zone imprimable.");
                        if (y + rowHeight > bottom + 0.01 && pages[^1].Used) Advance();
                        for (var c = 0; c < tableColumns; c++)
                        {
                            var x = Left() + indent + c * cellWidth; pages[^1].Cells.Add(new(new Rect(x, y, cellWidth, rowHeight), head));
                            var lineY = y + pad; foreach (var line in cells[c].TextLines) { pages[^1].Lines.Add(new(line, new Point(x + pad, lineY), p["id"]!.GetValue<string>())); lineY += line.Height; }
                        }
                        y += rowHeight;
                    }
                    y += style["spacing_after"]!.GetValue<double>() * 4 / 3; continue;
                }
                if (p["image"] is JsonObject image)
                {
                    var bitmap = Decode(image); var natural = new Size(image["width"]!.GetValue<double>(), image["height"]!.GetValue<double>());
                    var area = Settings.Height - Settings.Top - Settings.Bottom; var scale = Math.Min(1, Math.Min(width / natural.Width, area / natural.Height));
                    var drawn = new Size(natural.Width * scale, natural.Height * scale);
                    if (y + drawn.Height > bottom + 0.01 && pages[^1].Used) Advance();
                    var left = Left() + indent + style["alignment"]!.GetValue<string>() switch { "center" => (width - drawn.Width) / 2, "right" => width - drawn.Width, _ => 0 };
                    if (bitmap is not null) pages[^1].Pictures.Add(new(bitmap, new Rect(new Point(left, y), drawn)));
                    y += drawn.Height + style["spacing_after"]!.GetValue<double>() * 4 / 3; continue;
                }
                var size = style["font_size"]!.GetValue<double>(); var largest = size;
                var intervals = new List<ValueSpan<TextRunProperties>>(); var cursor = 0; var normal = Properties(style);
                if (p["runs"] is JsonArray runs) foreach (var r in runs)
                {
                    var s = r!["style"]!; largest = Math.Max(largest, s["font_size"]!.GetValue<double>());
                    var start = r["start"]!.GetValue<int>(); var length = r["length"]!.GetValue<int>();
                    if (start > cursor) intervals.Add(new(cursor, start - cursor, normal));
                    intervals.Add(new(start, length, Properties(s))); cursor = start + length;
                }
                if (cursor < text.Length) intervals.Add(new(cursor, text.Length - cursor, normal));
                var layout = new TextLayout(text, Typeface(style), size, Brush.Parse(style["color"]!.GetValue<string>()),
                    textAlignment: style["alignment"]!.GetValue<string>() switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, "justify" => TextAlignment.Justify, _ => TextAlignment.Left },
                    textWrapping: TextWrapping.Wrap, maxWidth: width, lineHeight: largest * style["line_spacing"]!.GetValue<double>(), textStyleOverrides: intervals);
                shaped.Add(layout);
                var first = true; var shading = style["shading"]?.GetValue<string>() ?? "none"; var border = style["border"]?.GetValue<string>() ?? "none"; Frame? frame = null;
                foreach (var line in layout.TextLines)
                {
                    if (line.Height > bottom - Settings.Top + 0.01) throw new InvalidOperationException("Une ligne est plus haute que la zone imprimable.");
                    if (y + line.Height > bottom + 0.01 && pages[^1].Used) Advance();
                    if (first && list != "none")
                    {
                        var marker = new TextLayout(list == "bullet" ? "•" : numbered + ".", Typeface(style), size, Brush.Parse(style["color"]!.GetValue<string>()));
                        shaped.Add(marker); pages[^1].Markers.Add(new(marker, new Point(Left() + indent, y)));
                    }
                    if (shading != "none" || border != "none")
                    {
                        var area = new Rect(Left() + indent - 4, y, width + markerWidth + 8, line.Height);
                        if (frame is null || !pages[^1].Frames.Contains(frame)) { frame = new Frame(area, shading, border); pages[^1].Frames.Add(frame); }
                        else frame.Bounds = frame.Bounds.Union(area);
                    }
                    pages[^1].Lines.Add(new(line, new Point(Left() + indent + markerWidth, y), p["id"]!.GetValue<string>()));
                    y += line.Height; first = false;
                }
                y += style["spacing_after"]!.GetValue<double>() * 4 / 3;
            }
        }
        catch { Dispose(); throw; }
        void NewPage() { if (pages.Count >= 2000) throw new InvalidOperationException("Le document dépasse 2 000 pages."); pages.Add(new()); y = Settings.Top; column = 0; }
        void Advance() { if (column < columns - 1) { column++; y = Settings.Top; } else NewPage(); }
    }
    private Bitmap? Decode(JsonNode image)
    {
        try { var bitmap = new Bitmap(new MemoryStream(Convert.FromBase64String(image["data"]!.GetValue<string>()))); bitmaps.Add(bitmap); return bitmap; }
        catch { return null; }
    }
    private static TextDecorationCollection? Decorations(bool underline, bool strike) { if (!underline && !strike) return null; var result = new TextDecorationCollection(); if (underline) result.Add(new TextDecoration { Location = TextDecorationLocation.Underline }); if (strike) result.Add(new TextDecoration { Location = TextDecorationLocation.Strikethrough }); return result; }
    private static Typeface Typeface(JsonNode s) => new(new FontFamily(s["font_family"]!.GetValue<string>()), s["italic"]!.GetValue<bool>() ? FontStyle.Italic : FontStyle.Normal, s["bold"]!.GetValue<bool>() ? FontWeight.Bold : FontWeight.Normal);
    private static TextRunProperties Properties(JsonNode s) => new GenericTextRunProperties(Typeface(s), s["font_size"]!.GetValue<double>() * (s["superscript"]!.GetValue<bool>() || s["subscript"]!.GetValue<bool>() ? 0.7 : 1),
        textDecorations: Decorations(s["underline"]!.GetValue<bool>(), s["strike"]!.GetValue<bool>()),
        baselineAlignment: s["superscript"]!.GetValue<bool>() ? BaselineAlignment.Superscript : s["subscript"]!.GetValue<bool>() ? BaselineAlignment.Subscript : BaselineAlignment.Baseline,
        foregroundBrush: Brush.Parse(s["color"]!.GetValue<string>()), backgroundBrush: s["highlight"]!.GetValue<string>() == "none" ? null : Brush.Parse(s["highlight"]!.GetValue<string>()));
    public void Draw(DrawingContext context, int page)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        context.DrawRectangle(Brushes.White, null, new Rect(0, 0, Settings.Width, Settings.Height));
        var rule = new Pen(Brushes.Gray, 1);
        foreach (var cell in pages[page].Cells) context.DrawRectangle(cell.Header ? new SolidColorBrush(Color.Parse("#F1F3F8")) : null, rule, cell.Bounds);
        var edge = new Pen(Brush.Parse("#808080"), 1);
        foreach (var frame in pages[page].Frames)
        {
            var b = frame.Bounds;
            if (frame.Fill != "none") context.DrawRectangle(Brush.Parse(frame.Fill), null, b);
            switch (frame.Border)
            {
                case "box": context.DrawRectangle(null, edge, b); break;
                case "bottom": context.DrawLine(edge, b.BottomLeft, b.BottomRight); break;
                case "left": context.DrawLine(new Pen(Brush.Parse("#808080"), 3), b.TopLeft, b.BottomLeft); break;
            }
        }
        foreach (var line in pages[page].Lines) line.Text.Draw(context, line.Origin);
        foreach (var marker in pages[page].Markers) marker.Text.Draw(context, marker.Origin);
        foreach (var picture in pages[page].Pictures) context.DrawImage(picture.Bitmap, new Rect(picture.Bitmap.Size), picture.Bounds);
        var footer = (Fill(Footer.Trim(), page) + (NumberPages ? (Footer.Trim().Length > 0 ? "  ·  " : "") + $"{page + 1} / {pages.Count}" : "")).Trim();
        DrawMargin(context, Fill(Header.Trim(), page), Settings.Top, false, Settings.HeaderAlign);
        DrawMargin(context, footer, Settings.Height - Settings.Bottom + 6, true, Settings.FooterAlign);
    }
    private string Fill(string text, int page) => text.Replace("{page}", (page + 1).ToString()).Replace("{pages}", pages.Count.ToString());
    private void DrawMargin(DrawingContext context, string text, double edge, bool below = false, string align = "center")
    {
        if (text.Length == 0) return;
        var room = below ? Settings.Bottom - 6 : Settings.Top - 6; if (room < 12) return;
        using var layout = new TextLayout(text, new Typeface(new FontFamily("Georgia")), Math.Min(11, room), Brushes.Gray, textAlignment: align switch { "left" => TextAlignment.Left, "right" => TextAlignment.Right, _ => TextAlignment.Center }, textWrapping: TextWrapping.NoWrap, textTrimming: TextTrimming.CharacterEllipsis, maxWidth: Settings.Width - Settings.Left - Settings.Right);
        layout.Draw(context, new Point(Settings.Left, below ? edge : edge - 6 - layout.Height));
    }
    public async Task ExportPdfAsync(string path, int firstPage = 1, int? lastPage = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var last = lastPage ?? pages.Count;
        if (firstPage < 1 || last > pages.Count || firstPage > last) throw new ArgumentOutOfRangeException(nameof(firstPage));
        var target = Path.GetFullPath(path); var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporary))
            using (var pdf = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { Title = Title, Creator = "Nexolume" }))
            {
                for (var i = firstPage - 1; i < last; i++)
                {
                    var canvas = pdf.BeginPage((float)(Settings.Width * .75), (float)(Settings.Height * .75));
                    var visual = new PrintPageControl(this, i, .75); visual.Measure(new Size(Settings.Width * .75, Settings.Height * .75)); visual.Arrange(new Rect(visual.DesiredSize));
                    await DrawingContextHelper.RenderAsync(canvas, visual);
                    pdf.EndPage(); await Task.Yield();
                }
                pdf.Close();
            }
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose() { if (disposed) return; disposed = true; foreach (var layout in shaped) layout.Dispose(); shaped.Clear(); foreach (var bitmap in bitmaps) bitmap.Dispose(); bitmaps.Clear(); }
}

public sealed class PrintPageControl(WriterPrintLayout layout, int page, double scale = 1) : Control
{
    protected override Size MeasureOverride(Size availableSize) => new(layout.Settings.Width * scale, layout.Settings.Height * scale);
    public override void Render(DrawingContext context) { using (context.PushTransform(Matrix.CreateScale(scale, scale))) layout.Draw(context, page); }
}
