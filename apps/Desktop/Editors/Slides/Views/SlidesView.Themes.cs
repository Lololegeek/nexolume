using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Nexolume.Desktop.Editors.Slides.Printing;
using System.Text.Json.Nodes;

namespace Nexolume.Desktop.Editors.Slides.Views;

public partial class SlidesView
{
    private sealed record DeckTheme(string Name, string Background, string Ink, string Accent, string Secondary);
    private static readonly DeckTheme[] DeckThemes = [
        new("Nexolume", "#FFFFFF", "#243249", "#285CB3", "#C97F4A"),
        new("Minuit", "#1B2A41", "#FFFFFF", "#77ACFF", "#F4BB77"),
        new("Forêt", "#E3F1E7", "#183C30", "#2D806C", "#B37B42"),
        new("Papier", "#FFF8E7", "#3C3429", "#A54A38", "#8A8543")
    ];

    private void SetupThemesAndExport()
    {
        var themeBox = this.FindControl<ComboBox>("DeckThemeBox")!; themeBox.ItemsSource = DeckThemes.Select(t => t.Name).ToArray(); themeBox.SelectedIndex = 0;
        this.FindControl<Button>("ApplyDeckThemeButton")!.Click += async (_, _) => { if (themeBox.SelectedItem is string name) await ApplyDeckThemeAsync(name, this.FindControl<CheckBox>("ThemeAllSlides")!.IsChecked == true); };
        this.FindControl<Button>("ApplySlideDesignButton")!.Click += async (_, _) =>
        {
            await FlushAsync(); var background = Slide["background"]?.GetValue<string>() ?? "#FFFFFF"; var transition = Slide["transition"]?.GetValue<string>() ?? "none";
            await Enqueue(() => Document.ExecuteAsync("Batch", new { commands = Slides.Select(s => new { command = "UpdateSlide", @params = new { id = s!["id"]!.GetValue<string>(), background, transition } }).ToArray() }));
        };
        var align = new MenuFlyout();
        foreach (var (label, value) in new[] { ("Aligner à gauche", "left"), ("Centrer horizontalement", "center"), ("Aligner à droite", "right"), ("Aligner en haut", "top"), ("Centrer verticalement", "middle"), ("Aligner en bas", "bottom") })
        {
            var item = new MenuItem { Header = label }; item.Click += async (_, _) => await AlignSelectedObjectAsync(value); align.Items.Add(item);
        }
        this.FindControl<Button>("AlignSlideObjectButton")!.Flyout = align;
        var exports = new MenuFlyout();
        foreach (var (label, pdf) in new[] { ("Présentation en PDF…", true), ("Diapositive en PNG (1920 × 1080)…", false) })
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) =>
            {
                await FlushAsync();
                await Enqueue(async () =>
                {
                    var extension = pdf ? "pdf" : "png"; var slides = Slides; var slide = Slide;
                    var path = await Platform.AppPlatform.Current.SaveFileAsync(this, new() { Title = label, SuggestedFileName = Document.Title + "." + extension, DefaultExtension = extension, ShowOverwritePrompt = true, FileTypeChoices = [new(pdf ? "Document PDF" : "Image PNG") { Patterns = ["*." + extension] }] }, pdf ? "application/pdf" : "image/png", stream => pdf ? SlideExporter.WritePdfAsync(slides, Document.Title, stream) : SlideExporter.WritePngAsync(slide, stream));
                    if (path is null) return;
                    this.FindControl<TextBlock>("SlideStatus")!.Text = "Export terminé : " + path;
                });
            };
            exports.Items.Add(item);
        }
        this.FindControl<Button>("ExportSlidesButton")!.Flyout = exports;
    }

    public async Task ApplyDeckThemeAsync(string name, bool allSlides)
    {
        await FlushAsync();
        await Enqueue(async () =>
        {
            var theme = DeckThemes.First(t => t.Name == name); var commands = new List<object>();
            foreach (var slide in allSlides ? Slides.OfType<JsonNode>() : new[] { Slide })
            {
                var id = slide["id"]!.GetValue<string>();
                commands.Add(new { command = "UpdateSlide", @params = new { id, background = theme.Background } });
                var index = 0;
                foreach (var obj in slide["objects"]!.AsArray())
                {
                    var kind = obj!["kind"]!.GetValue<string>(); if (kind == "image") continue;
                    var fill = kind == "text" ? theme.Ink : index++ % 2 == 0 ? theme.Accent : theme.Secondary;
                    commands.Add(new { command = "UpdateObject", @params = new { slide = id, id = obj["id"]!.GetValue<string>(), fill } });
                }
            }
            if (commands.Count > 1000) throw new InvalidOperationException("La présentation est trop grande : appliquez le thème diapositive par diapositive.");
            await Document.ExecuteAsync("Batch", new { commands });
        });
    }

    public async Task AlignSelectedObjectAsync(string alignment)
    {
        await FlushAsync(); if (activeObject is null) return;
        await Enqueue(() => Document.ExecuteAsync("AlignObject", new { slide = slideId, id = activeObject, alignment }));
    }
}
