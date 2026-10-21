using Avalonia;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using System.Text.Json.Nodes;

namespace Nexolume.Desktop.Editors.Writer.Controls;

public sealed class RichTextPresenter(RichParagraphBox owner) : TextPresenter
{
    private Size constraint;
    public void RefreshFormatting() => InvalidateTextLayout();
    protected override Size MeasureOverride(Size availableSize) { constraint = availableSize; return base.MeasureOverride(availableSize); }
    protected override Size ArrangeOverride(Size finalSize) { constraint = new Size(finalSize.Width, double.PositiveInfinity); return base.ArrangeOverride(finalSize); }
    protected override TextLayout CreateTextLayout()
    {
        if (owner.Runs.Count == 0) return base.CreateTextLayout();
        var text = Text ?? ""; var caret = Math.Clamp(CaretIndex, 0, text.Length); var preedit = PreeditText ?? "";
        if (preedit.Length > 0) text = text.Insert(caret, preedit);
        var points = new SortedSet<int> { 0, text.Length };
        foreach (var run in owner.Runs)
        {
            var start = run!["start"]!.GetValue<int>(); var end = start + run["length"]!.GetValue<int>();
            points.Add(Math.Clamp(start >= caret ? start + preedit.Length : start, 0, text.Length)); points.Add(Math.Clamp(end > caret ? end + preedit.Length : end, 0, text.Length));
        }
        var selectedStart = Math.Clamp(Math.Min(SelectionStart, SelectionEnd), 0, text.Length); var selectedEnd = Math.Clamp(Math.Max(SelectionStart, SelectionEnd), 0, text.Length);
        points.Add(selectedStart); points.Add(selectedEnd); if (preedit.Length > 0) { points.Add(caret); points.Add(caret + preedit.Length); }
        var sorted = points.ToArray(); var overrides = new List<ValueSpan<TextRunProperties>>(); var largest = FontSize;
        for (int i = 0; i < sorted.Length - 1; i++)
        {
            var start = sorted[i]; var end = sorted[i + 1]; if (start == end) continue;
            var original = start >= caret + preedit.Length ? start - preedit.Length : start >= caret ? Math.Max(0, caret - 1) : start;
            var style = owner.StyleAt(original);
            var typeface = new Typeface(new FontFamily(style["font_family"]!.GetValue<string>()), style["italic"]!.GetValue<bool>() ? FontStyle.Italic : FontStyle.Normal, style["bold"]!.GetValue<bool>() ? FontWeight.Bold : FontWeight.Normal);
            var size = style["font_size"]!.GetValue<double>(); var raised = style["superscript"]!.GetValue<bool>(); var lowered = style["subscript"]!.GetValue<bool>(); if (raised || lowered) size *= 0.7; largest = Math.Max(largest, size);
            var foreground = ShowSelectionHighlight && preedit.Length == 0 && start >= selectedStart && end <= selectedEnd && selectedStart != selectedEnd ? SelectionForegroundBrush ?? Brush.Parse(style["color"]!.GetValue<string>()) : Brush.Parse(style["color"]!.GetValue<string>());
            var highlight = style["highlight"]!.GetValue<string>();
            overrides.Add(new(start, end - start, new GenericTextRunProperties(typeface, FontFeatures, size, textDecorations: Decorations(style["underline"]!.GetValue<bool>() || (preedit.Length > 0 && start >= caret && end <= caret + preedit.Length), style["strike"]!.GetValue<bool>()), baselineAlignment: raised ? BaselineAlignment.Superscript : lowered ? BaselineAlignment.Subscript : BaselineAlignment.Baseline, foregroundBrush: foreground, backgroundBrush: highlight == "none" ? null : Brush.Parse(highlight))));
        }
        var lineHeight = double.IsNaN(LineHeight) ? double.NaN : Math.Max(LineHeight, largest * LineHeight / FontSize);
        return new TextLayout(text, new Typeface(FontFamily, FontStyle, FontWeight), FontFeatures, FontSize, Foreground, TextAlignment, TextWrapping, maxWidth: constraint.Width <= 0 ? double.PositiveInfinity : constraint.Width, maxHeight: double.PositiveInfinity, textStyleOverrides: overrides, flowDirection: FlowDirection, lineHeight: lineHeight, letterSpacing: LetterSpacing);
    }
    private static TextDecorationCollection? Decorations(bool underline, bool strike) { if (!underline && !strike) return null; var result = new TextDecorationCollection(); if (underline) result.Add(new TextDecoration { Location = TextDecorationLocation.Underline }); if (strike) result.Add(new TextDecoration { Location = TextDecorationLocation.Strikethrough }); return result; }
}
