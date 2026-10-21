using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using System.Text.Json.Nodes;
namespace Nexolume.Desktop.Editors.Slides.Views;

public partial class SlidesView
{
    private static readonly (string Label, string Value)[] Backgrounds = [("Blanc", "#FFFFFF"), ("Crème", "#FFF8E7"), ("Gris clair", "#EEF1F5"), ("Bleu clair", "#DCE8FC"), ("Vert clair", "#E3F1E7"), ("Bleu nuit", "#1B2A41"), ("Noir", "#111111")];
    private static readonly (string Label, string Value)[] Transitions = [("Aucune", "none"), ("Fondu", "fade"), ("Glissement", "slide")];
    private static readonly (string Label, string Value)[] LineColors = [("Sans contour", "none"), ("Noir", "#111111"), ("Bleu", "#285CB3"), ("Rouge", "#C00000"), ("Blanc", "#FFFFFF"), ("Gris", "#7F7F7F")];
    private static readonly (string Label, string Kind)[] ExtraShapes = [("Rectangle arrondi", "rounded"), ("Triangle", "triangle"), ("Losange", "diamond"), ("Ligne", "line")];
    private bool notesDraft;

    private void SetupSlideDesign()
    {
        var shapes = new StackPanel { Spacing = 6, Margin = new Avalonia.Thickness(6) }; var flyout = new Flyout { Content = shapes };
        foreach (var (label, kind) in ExtraShapes) { var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch }; button.Click += async (_, _) => { flyout.Hide(); await AddObjectAsync(kind); }; shapes.Children.Add(button); }
        this.FindControl<Button>("ShapesButton")!.Flyout = flyout;

        void Toggle(string name, string key) => this.FindControl<Button>(name)!.Click += async (_, _) =>
        {
            await FlushAsync(); if (activeObject is null) return;
            var selected = Surface.Objects.First(o => o!["id"]!.GetValue<string>() == activeObject)!;
            await SetObjectAsync(key, !(selected[key]?.GetValue<bool>() ?? false));
        };
        Toggle("ObjectItalicButton", "italic"); Toggle("ObjectUnderlineButton", "underline");
        foreach (var (name, value) in new[] { ("ObjectAlignLeftButton", "left"), ("ObjectAlignCenterButton", "center"), ("ObjectAlignRightButton", "right") }) this.FindControl<Button>(name)!.Click += async (_, _) => await SetObjectAsync("align", value);

        void Number(string name, string key, double scale) => this.FindControl<NumericUpDown>(name)!.ValueChanged += async (_, _) =>
        {
            if (rendering || this.FindControl<NumericUpDown>(name)!.Value is not { } value) return;
            await SetObjectAsync(key, (double)value / scale);
        };
        Number("ObjectRotation", "rotation", 1); Number("ObjectOpacity", "opacity", 100); Number("ObjectLineWidth", "line_width", 1);

        Combo("ObjectLineColor", LineColors, async value => await SetObjectAsync("line", value));
        Combo("SlideBackgroundBox", Backgrounds, async value => await SetSlideAsync("background", value));
        Combo("SlideTransitionBox", Transitions, async value => await SetSlideAsync("transition", value));
        var notes = this.FindControl<TextBox>("SlideNotes")!;
        notes.TextChanged += (_, _) => { if (rendering) return; notesDraft = (notes.Text ?? "") != (Slide["notes"]?.GetValue<string>() ?? ""); DraftUpdated(); };
        notes.LostFocus += async (_, _) => await FlushAsync();
    }

    private void Combo(string name, (string Label, string Value)[] list, Func<string, Task> apply)
    {
        var box = this.FindControl<ComboBox>(name)!;
        foreach (var (label, value) in list) box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        box.SelectedIndex = 0;
        box.SelectionChanged += async (_, _) => { if (rendering || box.SelectedItem is not ComboBoxItem item) return; await apply((string)item.Tag!); };
    }

    private async Task SetObjectAsync(string key, object value)
    {
        await FlushAsync(); if (activeObject is null) return;
        var parameters = new Dictionary<string, object> { ["slide"] = slideId, ["id"] = activeObject, [key] = value };
        await Enqueue(() => Document.ExecuteAsync("UpdateObject", parameters));
    }

    private async Task SetSlideAsync(string key, object value)
    {
        await FlushAsync();
        var parameters = new Dictionary<string, object> { ["id"] = slideId, [key] = value };
        await Enqueue(() => Document.ExecuteAsync("UpdateSlide", parameters));
    }

    private async Task FlushNotesAsync()
    {
        var text = this.FindControl<TextBox>("SlideNotes")!.Text ?? "";
        if (!notesDraft && text == (Slide["notes"]?.GetValue<string>() ?? "")) return;
        notesDraft = false; var slide = slideId;
        await Enqueue(() => Document.ExecuteAsync("UpdateSlide", new { id = slide, notes = text })); if (LastError is not null) notesDraft = true; DraftUpdated();
    }

    private void LoadObjectDesign(JsonNode? o)
    {
        this.FindControl<NumericUpDown>("ObjectRotation")!.Value = (decimal)(o?["rotation"]?.GetValue<double>() ?? 0);
        this.FindControl<NumericUpDown>("ObjectOpacity")!.Value = (decimal)Math.Round((o?["opacity"]?.GetValue<double>() ?? 1) * 100);
        this.FindControl<NumericUpDown>("ObjectLineWidth")!.Value = (decimal)(o?["line_width"]?.GetValue<double>() ?? 2);
        Select("ObjectLineColor", o?["line"]?.GetValue<string>() ?? "none");
        this.FindControl<Button>("ObjectItalicButton")!.Classes.Set("selected", o?["italic"]?.GetValue<bool>() == true);
        this.FindControl<Button>("ObjectUnderlineButton")!.Classes.Set("selected", o?["underline"]?.GetValue<bool>() == true);
        var align = o?["align"]?.GetValue<string>() ?? "left";
        foreach (var (name, value) in new[] { ("ObjectAlignLeftButton", "left"), ("ObjectAlignCenterButton", "center"), ("ObjectAlignRightButton", "right") }) this.FindControl<Button>(name)!.Classes.Set("selected", align == value);
    }

    private void LoadSlideProperties()
    {
        Select("SlideBackgroundBox", Slide["background"]?.GetValue<string>() ?? "#FFFFFF"); Select("SlideTransitionBox", Slide["transition"]?.GetValue<string>() ?? "none");
        if (!notesDraft) this.FindControl<TextBox>("SlideNotes")!.Text = Slide["notes"]?.GetValue<string>() ?? "";
    }

    private void Select(string name, string value)
    {
        var box = this.FindControl<ComboBox>(name)!;
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag!, value, StringComparison.OrdinalIgnoreCase)) ?? box.Items[0];
    }
}
