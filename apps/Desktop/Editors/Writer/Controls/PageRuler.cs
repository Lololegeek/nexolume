using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Globalization;
namespace Nexolume.Desktop.Editors.Writer.Controls;

public sealed class PageRuler : Control
{
    public double PaperWidth { get; set; } = 793.7;
    public double LeftMargin { get; set; } = 96;
    public double RightMargin { get; set; } = 96;
    public override void Render(DrawingContext context)
    {
        var scale = Bounds.Width / PaperWidth; var pen = new Pen(Brush.Parse("#818B9B"), 1); var ink = Brush.Parse("#586376");
        context.FillRectangle(Brush.Parse("#E9EDF3"), new Rect(Bounds.Size));
        context.FillRectangle(Brushes.White, new Rect(LeftMargin * scale, 0, (PaperWidth - LeftMargin - RightMargin) * scale, Bounds.Height));
        for (int half = 0; half <= 120; half++) { var x = (LeftMargin + half * 18.8976) * scale; if (x > Bounds.Width - RightMargin * scale) break; context.DrawLine(pen, new Point(x, Bounds.Height), new Point(x, Bounds.Height - (half % 2 == 0 ? 6 : 3))); if (half % 2 == 0 && half > 0) context.DrawText(new FormattedText((half / 2).ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, ink), new Point(x - 3, 1)); }
    }
}
