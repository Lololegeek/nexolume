using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Text.Json.Nodes;

namespace Nexolume.Desktop.Editors.Writer.Views;

public partial class WriterView
{
    private record NamedStyle(string Label, double Size, bool Bold, bool Italic, string Color, string? Family = null, int Indent = 0, string Alignment = "left");

    private static readonly NamedStyle[] NamedStyles =
    [
        new("Normal", 16, false, false, "#111111"),
        new("Titre", 40, true, false, "#111111"),
        new("Sous-titre", 21, false, true, "#5A5A5A"),
        new("Titre 1", 30, true, false, "#1F3864"),
        new("Titre 2", 23, true, false, "#2F5496"),
        new("Titre 3", 20, true, false, "#1F4E79"),
        new("Citation", 16, false, true, "#444444", null, 1),
        new("Citation intense", 17, true, true, "#2F5496", null, 1, "center"),
        new("Légende", 13, false, true, "#595959"),
        new("Code", 14, false, false, "#8B1A1A", "Consolas"),
    ];

    private JsonObject? painted;

    private void SetupStyles()
    {
        var panel = new StackPanel { Spacing = 2, Width = 250, Margin = new Thickness(2) };
        panel.Children.Add(new TextBlock { Text = "Styles", FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(4, 2, 4, 6) });
        var flyout = new Flyout { Content = new ScrollViewer { Content = panel, MaxHeight = 440 } };
        foreach (var style in NamedStyles)
        {
            var preview = new TextBlock { Text = style.Label, FontSize = Math.Min(style.Size, 26) * 0.9, FontWeight = style.Bold ? FontWeight.Bold : FontWeight.Normal, FontStyle = style.Italic ? FontStyle.Italic : FontStyle.Normal, Foreground = Brush.Parse(style.Color), FontFamily = new FontFamily(style.Family ?? "Aptos"), TextTrimming = TextTrimming.CharacterEllipsis };
            var item = new Button { Name = "Style_" + style.Label.Replace(' ', '_'), Content = preview, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 6), Classes = { "quiet" } };
            item.Click += async (_, _) => { flyout.Hide(); await ApplyNamedStyleAsync(style); };
            panel.Children.Add(item);
        }
        var more = this.FindControl<Button>("MoreStylesButton")!; more.Flyout = flyout;
        this.FindControl<Button>("FormatPainterButton")!.Click += async (_, _) => await FormatPainterAsync();
    }

    private async Task ApplyNamedStyleAsync(NamedStyle style) => await ChangeStyleAsync(s =>
    {
        s["font_size"] = style.Size; s["bold"] = style.Bold; s["italic"] = style.Italic; s["underline"] = false; s["color"] = style.Color;
        s["font_family"] = style.Family ?? (s["font_family"]!.GetValue<string>() == "Consolas" ? "Aptos" : s["font_family"]!.GetValue<string>());
        s["indent"] = style.Indent; s["alignment"] = style.Alignment;
    }, true);

    public Task ApplyNamedStyleAsync(string label) => ApplyNamedStyleAsync(NamedStyles.First(s => s.Label == label));

    // First click copies the formatting at the caret; the next click applies it to the current selection or paragraph.
    public async Task FormatPainterAsync()
    {
        var button = this.FindControl<Button>("FormatPainterButton")!;
        if (painted is null)
        {
            if (active is null || !boxes.TryGetValue(active, out var box)) return;
            var p = Paras[IndexOf(active)]!; var copy = (JsonObject)EffectiveStyle(p).DeepClone();
            foreach (var property in box.StyleAt(Math.Min(box.SelectionStart, Math.Max(0, (box.Text ?? "").Length - 1)))) copy[property.Key] = property.Value!.DeepClone();
            painted = copy; button.Classes.Set("selected", true); SetStatus("Mise en forme copiée : sélectionnez le texte à modifier puis cliquez de nouveau."); return;
        }
        var source = painted; painted = null; button.Classes.Set("selected", false);
        await ChangeStyleAsync(s => { foreach (var key in new[] { "bold", "italic", "underline", "strike", "superscript", "subscript", "small_caps", "font_family", "font_size", "color", "highlight" }) if (source[key] is { } value) s[key] = value.DeepClone(); });
        SetStatus("Mise en forme appliquée.");
    }
}
