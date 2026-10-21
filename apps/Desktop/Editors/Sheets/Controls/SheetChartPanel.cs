using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Globalization;
using System.Text.Json.Nodes;
namespace Nexolume.Desktop.Editors.Sheets.Controls;

/// <summary>Draws the charts of a sheet, stacked vertically: the first column of each range holds labels, the others hold series.</summary>
public sealed class SheetChartPanel : Control
{
    public const double ChartHeight = 260;
    private static readonly string[] Palette = { "#287861", "#C97F4A", "#285CB3", "#8A5CB5", "#B04A5A", "#6B8A7E" };
    public JsonObject Cells { get; set; } = new();
    public JsonArray Charts { get; set; } = new();
    public void Refresh() { InvalidateMeasure(); InvalidateVisual(); }
    protected override Size MeasureOverride(Size availableSize) => new(availableSize.Width is double.PositiveInfinity ? 380 : availableSize.Width, Math.Max(ChartHeight, Charts.Count * ChartHeight));

    public static (string[] Labels, string[] Names, List<double[]> Series) Read(JsonObject cells, string range)
    {
        var area = SheetGrid.ParseRange(range); var labels = new List<string>(); var series = new List<List<double>>();
        double? Number(JsonNode? cell) => cell is not null && GridRule.TryNumber(cell, out var n) ? n : null;
        int valueColumns = area.Right - area.Left + (area.Right > area.Left ? 0 : 1); int firstValue = area.Right > area.Left ? area.Left + 1 : area.Left;
        for (int c = 0; c < valueColumns; c++) series.Add(new());
        var names = Enumerable.Range(0, valueColumns).Select(i => "Série " + (i + 1)).ToArray();
        int top = area.Top;
        if (area.Bottom > area.Top && Enumerable.Range(0, valueColumns).All(c => Number(cells[SheetGrid.CellName(firstValue + c, area.Top)]) is null) && Enumerable.Range(0, valueColumns).Any(c => cells[SheetGrid.CellName(firstValue + c, area.Top)] is not null))
        {
            for (int c = 0; c < valueColumns; c++) names[c] = cells[SheetGrid.CellName(firstValue + c, area.Top)]?["display"]?.GetValue<string>() ?? names[c];
            top++;
        }
        for (int r = top; r <= area.Bottom && r - top < 200; r++)
        {
            labels.Add(area.Right > area.Left ? cells[SheetGrid.CellName(area.Left, r)]?["display"]?.GetValue<string>() ?? "" : (r - top + 1).ToString());
            for (int c = 0; c < valueColumns; c++) series[c].Add(Number(cells[SheetGrid.CellName(firstValue + c, r)]) ?? 0);
        }
        return (labels.ToArray(), names, series.Select(s => s.ToArray()).ToList());
    }

    public override void Render(DrawingContext context)
    {
        var ink = Brush.Parse("#23382F"); var muted = Brush.Parse("#6B7C76"); var line = new Pen(Brush.Parse("#D5DEDA"), 1);
        FormattedText Text(string text, double size, IBrush brush, bool bold = false) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI", FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), size, brush);
        for (int i = 0; i < Charts.Count; i++)
        {
            var chart = Charts[i]!; var box = new Rect(14, i * ChartHeight + 12, Bounds.Width - 28, ChartHeight - 24);
            context.DrawText(Text(chart["title"]?.GetValue<string>() is { Length: > 0 } t ? t : "Graphique", 14, ink, true), new Point(box.X, box.Y));
            var (labels, names, series) = Read(Cells, chart["range"]!.GetValue<string>()); var kind = chart["kind"]!.GetValue<string>();
            var plot = new Rect(box.X + 34, box.Y + 30, box.Width - 40, box.Height - 80);
            if (labels.Length == 0 || series.Count == 0) { context.DrawText(Text("Aucune donnée dans la plage", 12, muted), new Point(box.X, box.Y + 40)); continue; }
            if (kind == "pie")
            {
                var values = series[0].Select(Math.Abs).ToArray(); var total = values.Sum(); if (total <= 0) continue;
                var center = new Point(plot.X + plot.Height / 2 + 4, plot.Y + plot.Height / 2); var radius = plot.Height / 2; double start = -Math.PI / 2;
                for (int k = 0; k < values.Length; k++)
                {
                    double sweep = values[k] / total * Math.PI * 2; var geometry = new StreamGeometry();
                    using (var g = geometry.Open())
                    {
                        g.BeginFigure(center, true); g.LineTo(new Point(center.X + radius * Math.Cos(start), center.Y + radius * Math.Sin(start)));
                        g.ArcTo(new Point(center.X + radius * Math.Cos(start + sweep), center.Y + radius * Math.Sin(start + sweep)), new Size(radius, radius), 0, sweep > Math.PI, SweepDirection.Clockwise); g.EndFigure(true);
                    }
                    context.DrawGeometry(Brush.Parse(Palette[k % Palette.Length]), new Pen(Brushes.White, 1), geometry); start += sweep;
                    if (k < 8) { var ly = plot.Y + k * 16; context.FillRectangle(Brush.Parse(Palette[k % Palette.Length]), new Rect(plot.X + plot.Height + 24, ly + 3, 10, 10)); context.DrawText(Text($"{Trim(labels[k], 14)} · {values[k] / total:P0}", 11, ink), new Point(plot.X + plot.Height + 40, ly)); }
                }
                continue;
            }
            double max = Math.Max(0, series.SelectMany(s => s).DefaultIfEmpty(0).Max()), min = Math.Min(0, series.SelectMany(s => s).DefaultIfEmpty(0).Min()); if (max - min < 1e-9) max = min + 1;
            double Y(double v) => plot.Bottom - (v - min) / (max - min) * plot.Height;
            for (int g = 0; g <= 4; g++) { var v = min + (max - min) * g / 4; var y = Y(v); context.DrawLine(line, new Point(plot.X, y), new Point(plot.Right, y)); var label = Text(v.ToString("0.##", CultureInfo.CurrentCulture), 10, muted); context.DrawText(label, new Point(plot.X - label.Width - 4, y - 6)); }
            double slot = plot.Width / labels.Length;
            for (int k = 0; k < labels.Length; k++) { var label = Text(Trim(labels[k], Math.Max(3, (int)(slot / 6))), 10, muted); context.DrawText(label, new Point(plot.X + k * slot + (slot - label.Width) / 2, plot.Bottom + 6)); }
            if (kind == "bar")
            {
                double barWidth = Math.Max(2, slot * 0.7 / series.Count);
                for (int s = 0; s < series.Count; s++) for (int k = 0; k < labels.Length; k++)
                {
                    double x = plot.X + k * slot + slot * 0.15 + s * barWidth, y0 = Y(0), y1 = Y(series[s][k]);
                    context.FillRectangle(Brush.Parse(Palette[s % Palette.Length]), new Rect(x, Math.Min(y0, y1), barWidth - 1, Math.Max(1, Math.Abs(y0 - y1))));
                }
            }
            else
            {
                for (int s = 0; s < series.Count; s++)
                {
                    var pen = new Pen(Brush.Parse(Palette[s % Palette.Length]), 2); Point? previous = null;
                    for (int k = 0; k < labels.Length; k++) { var point = new Point(plot.X + k * slot + slot / 2, Y(series[s][k])); if (previous is { } p) context.DrawLine(pen, p, point); context.DrawEllipse(pen.Brush, null, point, 3, 3); previous = point; }
                }
            }
            if (series.Count > 1) for (int s = 0; s < series.Count; s++) { var x = box.X + s * 90; context.FillRectangle(Brush.Parse(Palette[s % Palette.Length]), new Rect(x, box.Bottom - 12, 10, 10)); context.DrawText(Text(Trim(names[s], 10), 11, ink), new Point(x + 14, box.Bottom - 15)); }
        }
    }
    private static string Trim(string text, int length) => text.Length <= length ? text : text[..Math.Max(1, length - 1)] + "…";
}
