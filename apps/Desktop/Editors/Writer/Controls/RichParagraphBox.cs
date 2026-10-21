using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Presenters;
using Avalonia.Data;
using Avalonia.Media;
using System.Text.Json.Nodes;

namespace Nexolume.Desktop.Editors.Writer.Controls;

public sealed class RichParagraphBox : TextBox
{
    public JsonArray Runs { get; private set; } = [];
    private JsonObject characterStyle = new() { ["bold"] = false, ["italic"] = false, ["underline"] = false, ["strike"] = false, ["superscript"] = false, ["subscript"] = false, ["small_caps"] = false, ["font_family"] = "Aptos", ["font_size"] = 16.0, ["color"] = "#111111", ["highlight"] = "none" };
    private RichTextPresenter? richPresenter;
    protected override Type StyleKeyOverride => typeof(TextBox);
    public RichParagraphBox()
    {
        ClearSelectionOnLostFocus = false;
        Template = new FuncControlTemplate<TextBox>((parent, scope) =>
        {
            var presenter = new RichTextPresenter(this) { Name = "PART_TextPresenter" }; richPresenter = presenter; scope.Register(presenter.Name, presenter);
            void Bind(AvaloniaProperty property, string path, BindingMode mode = BindingMode.OneWay) => presenter.Bind(property, new Binding(path) { Source = parent, Mode = mode });
            Bind(TextPresenter.TextProperty, nameof(Text)); Bind(TextPresenter.CaretIndexProperty, nameof(CaretIndex), BindingMode.TwoWay);
            Bind(TextPresenter.SelectionStartProperty, nameof(SelectionStart), BindingMode.TwoWay); Bind(TextPresenter.SelectionEndProperty, nameof(SelectionEnd), BindingMode.TwoWay);
            Bind(TextPresenter.CaretBrushProperty, nameof(CaretBrush)); Bind(TextPresenter.SelectionBrushProperty, nameof(SelectionBrush)); Bind(TextPresenter.SelectionForegroundBrushProperty, nameof(SelectionForegroundBrush));
            Bind(TextPresenter.TextWrappingProperty, nameof(TextWrapping)); Bind(TextPresenter.TextAlignmentProperty, nameof(TextAlignment)); Bind(TextPresenter.LineHeightProperty, nameof(LineHeight));
            Bind(TextElement.FontFamilyProperty, nameof(FontFamily)); Bind(TextElement.FontSizeProperty, nameof(FontSize)); Bind(TextElement.FontWeightProperty, nameof(FontWeight)); Bind(TextElement.FontStyleProperty, nameof(FontStyle)); Bind(TextElement.ForegroundProperty, nameof(Foreground));
            var watermark = new TextBlock { Foreground = Brush.Parse("#777777"), IsHitTestVisible = false };
            watermark.Bind(TextBlock.TextProperty, new Binding(nameof(Watermark)) { Source = parent }); watermark.Bind(TextBlock.FontSizeProperty, new Binding(nameof(FontSize)) { Source = parent });
            watermark.IsVisible = string.IsNullOrEmpty(parent.Text); parent.TextChanged += (_, _) => watermark.IsVisible = string.IsNullOrEmpty(parent.Text);
            var content = new Grid(); content.Children.Add(watermark); content.Children.Add(presenter);
            var scroll = new ScrollViewer { Name = "PART_ScrollViewer", Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            scope.Register(scroll.Name, scroll);
            var border = new Border { Name = "PART_BorderElement", Child = scroll };
            border.Bind(Border.PaddingProperty, new Binding(nameof(Padding)) { Source = parent }); border.Bind(Border.BackgroundProperty, new Binding(nameof(Background)) { Source = parent });
            return border;
        });
    }
    public static JsonObject Character(JsonNode style)
    {
        var result = new JsonObject();
        foreach (var key in new[] { "bold", "italic", "underline", "strike", "superscript", "subscript", "small_caps", "font_family", "font_size", "color", "highlight" }) result[key] = style[key]!.DeepClone();
        return result;
    }
    public JsonObject StyleAt(int offset)
    {
        foreach (var run in Runs) if (offset >= run!["start"]!.GetValue<int>() && offset < run["start"]!.GetValue<int>() + run["length"]!.GetValue<int>()) return (JsonObject)run["style"]!.DeepClone();
        return (JsonObject)characterStyle.DeepClone();
    }
    public void SetRichStyle(JsonNode style, JsonArray? runs)
    {
        characterStyle = Character(style); Runs = (JsonArray?)runs?.DeepClone() ?? [];
        richPresenter?.RefreshFormatting();
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == TextProperty && Runs is not null && characterStyle is not null)
        {
            var before = change.GetOldValue<string?>() ?? ""; var after = change.GetNewValue<string?>() ?? "";
            var prefix = 0; while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix]) prefix++;
            if (prefix > 0 && prefix < before.Length && char.IsLowSurrogate(before[prefix])) prefix--;
            var suffix = 0; while (suffix < before.Length - prefix && suffix < after.Length - prefix && before[^(suffix + 1)] == after[^(suffix + 1)]) suffix++;
            if (suffix > 0 && char.IsLowSurrogate(before[before.Length - suffix])) suffix--;
            AdjustRuns(prefix, before.Length - suffix, after.Length - prefix - suffix);
        }
        base.OnPropertyChanged(change);
    }
    private void AdjustRuns(int start, int end, int inserted)
    {
        var result = new JsonArray();
        void Add(int from, int length, JsonNode style)
        {
            if (length <= 0) return;
            if (result.LastOrDefault() is JsonObject last && last["start"]!.GetValue<int>() + last["length"]!.GetValue<int>() == from && JsonNode.DeepEquals(last["style"], style)) { last["length"] = last["length"]!.GetValue<int>() + length; return; }
            result.Add(new JsonObject { ["start"] = from, ["length"] = length, ["style"] = style.DeepClone() });
        }
        foreach (var run in Runs) { var from = run!["start"]!.GetValue<int>(); var to = from + run["length"]!.GetValue<int>(); Add(from, Math.Min(start, to) - from, run["style"]!); }
        var inherited = StyleAt(end > start ? start : Math.Max(0, start - 1)); if (!JsonNode.DeepEquals(inherited, characterStyle)) Add(start, inserted, inherited);
        foreach (var run in Runs) { var from = run!["start"]!.GetValue<int>(); var to = from + run["length"]!.GetValue<int>(); var left = Math.Max(end, from); Add(left + inserted - (end - start), to - left, run["style"]!); }
        Runs = result;
    }
}
