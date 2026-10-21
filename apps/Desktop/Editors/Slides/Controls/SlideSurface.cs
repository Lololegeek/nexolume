using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using System.Text.Json.Nodes;
namespace Nexolume.Desktop.Editors.Slides.Controls;

public sealed class SlideSurface : Control
{
    public JsonArray Objects { get; set; } = new();
    public string? Selected { get; private set; }
    public event Action? SelectionChanged;
    public event Action<string, Rect>? TransformCommitted;
    private bool dragging, resizing;
    private Point start;
    private Rect original;
    private Rect? preview;
    public SlideSurface() { Width = 960; Height = 540; Focusable = true; ClipToBounds = true; }
    public static Rect BoundsOf(JsonNode o) => new(o["x"]!.GetValue<double>(), o["y"]!.GetValue<double>(), o["width"]!.GetValue<double>(), o["height"]!.GetValue<double>());
    public void Select(string? id) { Selected = id; preview = null; SelectionChanged?.Invoke(); InvalidateVisual(); }
    public string Background { get; set; } = "#FFFFFF";
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brush.Parse(Background), new Rect(Bounds.Size));
        foreach (var o in Objects)
        {
            if (o is null) continue; var id = o["id"]!.GetValue<string>(); var rect = id == Selected && preview is { } p ? p : BoundsOf(o);
            var centre = rect.Center; var radians = (o["rotation"]?.GetValue<double>() ?? 0) * Math.PI / 180;
            using (context.PushTransform(Matrix.CreateTranslation(-centre.X, -centre.Y) * Matrix.CreateRotation(radians) * Matrix.CreateTranslation(centre.X, centre.Y)))
            {
                using (context.PushOpacity(o["opacity"]?.GetValue<double>() ?? 1)) DrawObject(context, o, rect);
                if (id == Selected) { context.DrawRectangle(null, new Pen(Brush.Parse("#285CB3"), 2), rect); context.DrawRectangle(Brushes.White, new Pen(Brush.Parse("#285CB3"), 2), new Rect(rect.Right - 6, rect.Bottom - 6, 12, 12)); }
            }
        }
    }
    private static readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap?> Pictures = new();
    private static Avalonia.Media.Imaging.Bitmap? Picture(JsonNode image)
    {
        var data = image["data"]!.GetValue<string>(); var key = data.Length + ":" + data.GetHashCode();
        if (Pictures.TryGetValue(key, out var cached)) return cached;
        if (Pictures.Count > 64) Pictures.Clear();
        try { return Pictures[key] = new Avalonia.Media.Imaging.Bitmap(new MemoryStream(Convert.FromBase64String(data))); } catch { return Pictures[key] = null; }
    }
    private static void DrawObject(DrawingContext context, JsonNode o, Rect rect)
    {
        var kind = o["kind"]!.GetValue<string>(); var fill = Brush.Parse(o["fill"]!.GetValue<string>());
        var lineColour = o["line"]?.GetValue<string>() ?? "none"; var lineWidth = o["line_width"]?.GetValue<double>() ?? 2;
        var outline = lineColour != "none" && lineWidth > 0 ? new Pen(Brush.Parse(lineColour), lineWidth) : null;
        switch (kind)
        {
            case "image":
                if (o["image"] is { } image && Picture(image) is { } bitmap) context.DrawImage(bitmap, new Rect(bitmap.Size), rect);
                else { context.DrawRectangle(Brush.Parse("#EEF2F0"), new Pen(Brush.Parse("#9AA8A2"), 1), rect); }
                if (outline is not null) context.DrawRectangle(null, outline, rect);
                break;
            case "rectangle": context.DrawRectangle(fill, outline, rect); break;
            case "rounded": context.DrawRectangle(fill, outline, rect, Math.Min(rect.Width, rect.Height) * 0.2, Math.Min(rect.Width, rect.Height) * 0.2); break;
            case "ellipse": context.DrawEllipse(fill, outline, rect); break;
            case "triangle": context.DrawGeometry(fill, outline, Polygon(new Point(rect.Center.X, rect.Top), new Point(rect.Right, rect.Bottom), new Point(rect.Left, rect.Bottom))); break;
            case "diamond": context.DrawGeometry(fill, outline, Polygon(new Point(rect.Center.X, rect.Top), new Point(rect.Right, rect.Center.Y), new Point(rect.Center.X, rect.Bottom), new Point(rect.Left, rect.Center.Y))); break;
            case "line": context.DrawLine(new Pen(lineColour != "none" ? Brush.Parse(lineColour) : fill, Math.Max(1, lineWidth), lineCap: PenLineCap.Round), new Point(rect.Left, rect.Center.Y), new Point(rect.Right, rect.Center.Y)); break;
            default:
                var fontSize = o["font_size"]?.GetValue<double>() ?? 28;
                var align = o["align"]?.GetValue<string>() switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, _ => TextAlignment.Left };
                var style = o["italic"]?.GetValue<bool>() == true ? FontStyle.Italic : FontStyle.Normal;
                using (var layout = new TextLayout(o["text"]!.GetValue<string>(), new Typeface("Aptos", style, o["bold"]?.GetValue<bool>() == true ? FontWeight.Bold : FontWeight.Normal), fontSize, fill, textAlignment: align, textWrapping: TextWrapping.Wrap, textDecorations: o["underline"]?.GetValue<bool>() == true ? TextDecorations.Underline : null, maxWidth: Math.Max(1, rect.Width - 20), maxHeight: Math.Max(1, rect.Height - 12)))
                {
                    using (context.PushClip(rect)) layout.Draw(context, new Point(rect.X + 10, rect.Y + 6));
                }
                if (outline is not null) context.DrawRectangle(null, outline, rect);
                break;
        }
    }
    private static StreamGeometry Polygon(params Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var c = geometry.Open()) { c.BeginFigure(points[0], true); foreach (var point in points.Skip(1)) c.LineTo(point); c.EndFigure(true); }
        return geometry;
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); var point = e.GetPosition(this); Focus(); var current = Objects.FirstOrDefault(o => o!["id"]!.GetValue<string>() == Selected);
        resizing = current is not null && new Rect(BoundsOf(current).Right - 12, BoundsOf(current).Bottom - 12, 24, 24).Contains(point);
        if (!resizing) { var hit = Objects.Reverse().FirstOrDefault(o => BoundsOf(o!).Contains(point)); Select(hit?["id"]!.GetValue<string>()); current = hit; }
        if (current is not null) { original = BoundsOf(current); preview = original; start = point; dragging = true; e.Pointer.Capture(this); }
        e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); if (!dragging) return; var delta = e.GetPosition(this) - start; preview = resizing ? new Rect(original.X, original.Y, Math.Clamp(original.Width + delta.X, 10, 5000), Math.Clamp(original.Height + delta.Y, 10, 5000)) : new Rect(Math.Clamp(original.X + delta.X, 0, 950), Math.Clamp(original.Y + delta.Y, 0, 530), original.Width, original.Height); InvalidateVisual(); }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); if (!dragging) return; dragging = false; e.Pointer.Capture(null); var rect = preview; preview = null; if (Selected is { } id && rect is { } final && final != original) TransformCommitted?.Invoke(id, final); InvalidateVisual(); e.Handled = true; }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); if (dragging) { dragging = false; preview = null; InvalidateVisual(); } }
}
