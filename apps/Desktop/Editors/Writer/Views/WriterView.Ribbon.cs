using Avalonia.Controls;
using Avalonia.Media;
using System.Text.Json.Nodes;
namespace Nexolume.Desktop.Editors.Writer.Views;

public partial class WriterView
{
    private bool updatingFont;
    private void SetupRibbon()
    {
        foreach (var key in new[] { "Home", "Insert", "Layout", "Review", "View" }) this.FindControl<Button>(key + "RibbonTab")!.Click += (_, _) =>
        {
            foreach (var name in new[] { "Home", "Insert", "Layout", "Review", "View" }) { this.FindControl<Grid>(name + "Ribbon")!.IsVisible = name == key; this.FindControl<Button>(name + "RibbonTab")!.Classes.Set("selected", name == key); }
        };
        var family = this.FindControl<ComboBox>("FontFamilyBox")!; SetupFonts();
        family.SelectionChanged += async (_, _) => { if (!updatingFont && family.SelectedItem is string font) { RememberFont(font); await ChangeStyleAsync(style => style["font_family"] = font); } };
        var size = this.FindControl<NumericUpDown>("FontSizeBox")!;
        size.ValueChanged += async (_, _) => { if (!updatingFont && size.Value is { } value) await ChangeStyleAsync(style => style["font_size"] = (double)value * 4 / 3); };
        foreach (var (name, value) in new[] { ("AlignLeftButton", "left"), ("AlignCenterButton", "center"), ("AlignRightButton", "right") }) this.FindControl<Button>(name)!.Click += async (_, _) => await ChangeStyleAsync(style => style["alignment"] = value);
        foreach (var (name, fontSize, bold) in new[] { ("NormalStyleButton", 16, false), ("HeadingStyleButton", 30, true), ("SubtitleStyleButton", 23, true) }) this.FindControl<Button>(name)!.Click += async (_, _) => await ChangeStyleAsync(style => { style["font_size"] = fontSize; style["bold"] = bold; style["italic"] = false; style["underline"] = false; }, true);
        this.FindControl<Button>("InsertParagraphButton")!.Click += async (_, _) => await AddParagraphAsync();
        this.FindControl<Button>("CopyButton")!.Click += (_, _) => { if (!TryMultiClipboard("copy") && active is not null) boxes[active].Copy(); };
        this.FindControl<Button>("CutButton")!.Click += (_, _) => { if (!TryMultiClipboard("cut") && active is not null) boxes[active].Cut(); };
        this.FindControl<Button>("PasteButton")!.Click += (_, _) => { if (!TryMultiClipboard("paste") && active is not null) boxes[active].Paste(); };
        var zoom = this.FindControl<Slider>("ZoomSlider")!;
        zoom.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) { this.FindControl<LayoutTransformControl>("PageTransform")!.LayoutTransform = new ScaleTransform(zoom.Value / 100, zoom.Value / 100); this.FindControl<TextBlock>("ZoomLabel")!.Text = $"{zoom.Value:0} %"; this.FindControl<Border>("PageRuler")!.Width = Printing.PageSettings.Read(Document.Snapshot).Width * zoom.Value / 100; this.FindControl<Border>("WordCompatibilityNote")!.Width = Printing.PageSettings.Read(Document.Snapshot).Width * zoom.Value / 100; } };
        this.FindControl<Button>("ZoomOutButton")!.Click += (_, _) => zoom.Value = Math.Max(50, zoom.Value - 10); this.FindControl<Button>("ZoomInButton")!.Click += (_, _) => zoom.Value = Math.Min(150, zoom.Value + 10);
        SetupOfficeTools();
        var ruler = this.FindControl<CheckBox>("RulerToggle")!; ruler.IsCheckedChanged += (_, _) => this.FindControl<Border>("PageRuler")!.IsVisible = ruler.IsChecked == true;
    }
    private void UpdateFontInputs(JsonNode style) { updatingFont = true; try { var font = style["font_family"]!.GetValue<string>(); var fonts = this.FindControl<ComboBox>("FontFamilyBox")!; if (fonts.ItemsSource is not List<string> known || !known.Contains(font)) RefreshFontList(font); fonts.SelectedItem = font; this.FindControl<NumericUpDown>("FontSizeBox")!.Value = (decimal)(style["font_size"]!.GetValue<double>() * 3 / 4); UpdateOfficeInputs(style); } finally { updatingFont = false; } }
    private async Task ChangeStyleAsync(Action<JsonNode> change, bool wholeParagraph = false)
    {
        if (multi is not null) { await ChangeStyleMultiAsync(change, wholeParagraph); return; }
        if (active is null) return; var id = active; var box = boxes[id]; var start = Math.Min(box.SelectionStart, box.SelectionEnd); var end = Math.Max(box.SelectionStart, box.SelectionEnd);
        var p = Document.Snapshot["content"]!["paragraphs"]!.AsArray().First(p => p!["id"]!.GetValue<string>() == id)!;
        var before = EffectiveStyle(p); var style = before.DeepClone(); change(style);
        var patch = new JsonObject(); foreach (var key in new[] { "bold", "italic", "underline", "strike", "superscript", "subscript", "small_caps", "font_family", "font_size", "color", "highlight" }) if (!JsonNode.DeepEquals(before[key], style[key])) patch[key] = style[key]!.DeepClone();
        await FlushAsync(); group = Guid.NewGuid().ToString();
        if (!wholeParagraph && start != end && patch.Count > 0) await Enqueue(() => Document.ExecuteAsync("FormatText", new { id, start, end, style = patch }));
        else { var paragraphStyle = p["style"]!.DeepClone(); change(paragraphStyle); await Enqueue(() => Document.ExecuteAsync("SetParagraph", new { id, style = paragraphStyle })); }
        box.Focus(); box.CaretIndex = end; box.SelectionStart = start; box.SelectionEnd = end;
    }
}
