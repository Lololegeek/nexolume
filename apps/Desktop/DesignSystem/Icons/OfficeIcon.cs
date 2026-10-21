using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
namespace Nexolume.Desktop.DesignSystem.Icons;

public sealed class OfficeIcon : Control
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<OfficeIcon, string>(nameof(Kind), "document");
    public static readonly StyledProperty<IBrush> StrokeProperty = AvaloniaProperty.Register<OfficeIcon, IBrush>(nameof(Stroke), Brushes.White);
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    private static readonly Dictionary<string, Geometry> Paths = new()
    {
        ["folder"] = Geometry.Parse("M3,7 L3,4 10,4 12,7 21,7 21,20 3,20 Z"),
        ["search"] = Geometry.Parse("M16,10 A6,6 0 1 1 4,10 A6,6 0 1 1 16,10 M15,15 L21,21"),
        ["sheets"] = Geometry.Parse("M3,3 L21,3 21,21 3,21 Z M3,9 L21,9 M3,15 L21,15 M9,3 L9,21 M15,3 L15,21"),
        ["slides"] = Geometry.Parse("M3,4 L21,4 21,17 3,17 Z M12,17 L12,22 M7,22 L17,22 M7,8 L17,8 M7,12 L13,12"),
        ["plus"] = Geometry.Parse("M12,4 L12,20 M4,12 L20,12"),
        ["trash"] = Geometry.Parse("M4,6 L20,6 M9,6 L9,3 15,3 15,6 M6,6 L7,21 17,21 18,6 M10,10 L10,17 M14,10 L14,17"),
        ["copy"] = Geometry.Parse("M8,8 L21,8 21,21 8,21 Z M4,16 L3,16 3,3 16,3 16,4"),
        ["check"] = Geometry.Parse("M4,12 L10,18 21,6"),
        ["arrow"] = Geometry.Parse("M4,12 L20,12 M14,6 L20,12 14,18"),
        ["save"] = Geometry.Parse("M5,3 L17,3 21,7 21,21 3,21 3,3 5,3 M7,3 L7,9 16,9 16,3 M7,21 L7,14 17,14 17,21"),
        ["undo"] = Geometry.Parse("M8,5 L3,10 8,15 M3,10 L14,10 C22,10 22,21 14,21"),
        ["redo"] = Geometry.Parse("M16,5 L21,10 16,15 M21,10 L10,10 C2,10 2,21 10,21"),
        ["paste"] = Geometry.Parse("M8,5 L4,5 4,22 20,22 20,5 16,5 M8,3 L16,3 16,7 8,7 Z M8,12 L16,12 M8,16 L15,16"),
        ["left"] = Geometry.Parse("M3,5 L21,5 M3,10 L15,10 M3,15 L21,15 M3,20 L15,20"),
        ["center"] = Geometry.Parse("M3,5 L21,5 M6,10 L18,10 M3,15 L21,15 M6,20 L18,20"),
        ["right"] = Geometry.Parse("M3,5 L21,5 M9,10 L21,10 M3,15 L21,15 M9,20 L21,20"),
        ["theme"] = Geometry.Parse("M12,3 A9,9 0 1 0 12,21 A9,9 0 0 0 12,3 M12,3 L12,21"),
        ["brand"] = Geometry.Parse("M4,20 L4,4 20,20 20,4 M10,4 L20,4 M4,20 L14,20"),
        ["document"] = Geometry.Parse("M5,2 L15,2 20,7 20,22 5,22 Z M14,2 L14,8 20,8 M9,12 L16,12 M9,16 L16,16"),
        ["cut"] = Geometry.Parse("M6,3 L15,15 M18,3 L9,15 M10,18 A3,3 0 1 1 4,18 A3,3 0 1 1 10,18 M20,18 A3,3 0 1 1 14,18 A3,3 0 1 1 20,18"),
        ["justify"] = Geometry.Parse("M3,5 L21,5 M3,10 L21,10 M3,15 L21,15 M3,20 L21,20"),
        ["bullets"] = Geometry.Parse("M9,6 L21,6 M9,12 L21,12 M9,18 L21,18 M3.5,6 L4.5,6 M3.5,12 L4.5,12 M3.5,18 L4.5,18"),
        ["numbers"] = Geometry.Parse("M10,6 L21,6 M10,12 L21,12 M10,18 L21,18 M3,5 L5,4 5,8 M3,14 L6,14 3,18 6,18"),
        ["indentMore"] = Geometry.Parse("M3,5 L21,5 M11,10 L21,10 M11,15 L21,15 M3,20 L21,20 M3,9 L7,12 3,15"),
        ["indentLess"] = Geometry.Parse("M3,5 L21,5 M11,10 L21,10 M11,15 L21,15 M3,20 L21,20 M7,9 L3,12 7,15"),
        ["eraser"] = Geometry.Parse("M4,16 L13,5 20,11 13,20 7,20 Z M8,10 L15,16 M10,20 L21,20"),
        ["outline"] = Geometry.Parse("M3,5 L5,5 M8,5 L21,5 M6,11 L8,11 M11,11 L21,11 M6,17 L8,17 M11,17 L21,17"),
        ["focus"] = Geometry.Parse("M4,9 L4,4 9,4 M15,4 L20,4 20,9 M20,15 L20,20 15,20 M9,20 L4,20 4,15"),
        ["spell"] = Geometry.Parse("M3,16 L7,5 11,16 M4.5,12 L9.5,12 M13,17 L16,20 21,11"),
        ["page"] = Geometry.Parse("M6,2 L18,2 18,22 6,22 Z M10,7 L14,7 M10,11 L14,11"),
        ["print"] = Geometry.Parse("M7,9 L7,3 17,3 17,9 M7,17 L4,17 4,9 20,9 20,17 17,17 M7,14 L17,14 17,21 7,21 Z"),
        ["pageBreak"] = Geometry.Parse("M6,2 L18,2 18,9 M6,2 L6,9 M3,12 L6,12 M9,12 L11,12 M13,12 L15,12 M18,12 L21,12 M6,15 L6,22 18,22 18,15"),
        ["case"] = Geometry.Parse("M2,18 L7,6 12,18 M4,14 L10,14 M14,12 A3,3 0 1 0 14,18 L14,12 M14,12 L14,18"),
        ["zoomIn"] = Geometry.Parse("M16,10 A6,6 0 1 1 4,10 A6,6 0 1 1 16,10 M15,15 L21,21 M10,7 L10,13 M7,10 L13,10"),
        ["zoomOut"] = Geometry.Parse("M16,10 A6,6 0 1 1 4,10 A6,6 0 1 1 16,10 M15,15 L21,21 M7,10 L13,10"),
        ["fit"] = Geometry.Parse("M4,4 L4,20 M20,4 L20,20 M8,12 L16,12 M11,9 L8,12 11,15 M13,9 L16,12 13,15"),
        ["styles"] = Geometry.Parse("M4,20 L9,5 14,20 M5.5,15 L12.5,15 M17,6 L21,6 M17,11 L21,11"),
        ["chevron"] = Geometry.Parse("M6,9 L12,15 18,9")
    };
    static OfficeIcon() { AffectsRender<OfficeIcon>(KindProperty, StrokeProperty); }
    public OfficeIcon() { Width = 18; Height = 18; IsHitTestVisible = false; }
    public override void Render(DrawingContext context) { if (!Paths.TryGetValue(Kind, out var path)) return; using (context.PushTransform(Matrix.CreateScale(Bounds.Width / 24, Bounds.Height / 24))) context.DrawGeometry(null, new Pen(Stroke, 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), path); }
}
