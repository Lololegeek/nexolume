using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Nexolume.Desktop.Editors.Slides.Controls;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Nexolume.Desktop.Editors.Slides.Views;

public partial class SlidesView
{
    private bool showThumbnails = true;
    private bool? showInspector;
    private void SetupSlidesProductivity()
    {
        SizeChanged += (_, _) => UpdateWorkspace();
        this.FindControl<Button>("ToggleThumbnailsButton")!.Click += (_, _) => { showThumbnails = !showThumbnails; UpdateWorkspace(); };
        this.FindControl<Button>("ToggleInspectorButton")!.Click += (_, _) => { showInspector = !this.FindControl<Border>("InspectorPanel")!.IsVisible; UpdateWorkspace(); };
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(6) };
        var flyout = new Flyout { Content = choices };
        foreach (var (key, label) in new[] { ("title", "Titre"), ("content", "Titre et contenu"), ("columns", "Deux colonnes") })
        {
            var preview = new SlideSurface { Objects = JsonSerializer.SerializeToNode(LayoutObjects(key))!.AsArray(), IsHitTestVisible = false };
            var button = new Button { Classes = { "layoutChoice" }, Content = new StackPanel { Spacing = 12, Children =
            {
                new Border { BorderBrush = Brush.Parse("#AFBBCD"), BorderThickness = new Thickness(1), Child = new Viewbox { Width = 176, Height = 99, Child = preview } },
                new TextBlock { Text = label, FontWeight = FontWeight.SemiBold }
            }}};
            button.Click += async (_, _) => { flyout.Hide(); await AddLayoutAsync(key); };
            choices.Children.Add(button);
        }
        this.FindControl<Button>("SlideLayoutButton")!.Flyout = flyout;
        this.FindControl<NumericUpDown>("ObjectFontSize")!.ValueChanged += async (_, _) =>
        {
            if (rendering || activeObject is null || this.FindControl<NumericUpDown>("ObjectFontSize")!.Value is not { } size) return;
            await FlushAsync(); await Enqueue(() => Document.ExecuteAsync("UpdateObject", new { slide = slideId, id = activeObject, font_size = (double)size }));
        };
        this.FindControl<Button>("ObjectBoldButton")!.Click += async (_, _) =>
        {
            await FlushAsync(); if (activeObject is null) return;
            var selected = Surface.Objects.First(o => o!["id"]!.GetValue<string>() == activeObject)!;
            await Enqueue(() => Document.ExecuteAsync("UpdateObject", new { slide = slideId, id = activeObject, bold = !(selected["bold"]?.GetValue<bool>() ?? false) }));
        };
    }
    private void UpdateWorkspace()
    {
        var inspector = showInspector ?? Bounds.Width >= 1200;
        var grid = this.FindControl<Grid>("SlidesWorkspace")!;
        grid.ColumnDefinitions[0].Width = new GridLength(showThumbnails ? 184 : 0);
        grid.ColumnDefinitions[1].Width = new GridLength(showThumbnails ? 5 : 0);
        grid.ColumnDefinitions[3].Width = new GridLength(inspector ? 5 : 0);
        grid.ColumnDefinitions[4].Width = new GridLength(inspector ? 268 : 0);
        this.FindControl<Border>("SlideNavigator")!.IsVisible = showThumbnails;
        this.FindControl<GridSplitter>("NavigatorSplitter")!.IsVisible = showThumbnails;
        this.FindControl<Border>("InspectorPanel")!.IsVisible = inspector;
        this.FindControl<GridSplitter>("InspectorSplitter")!.IsVisible = inspector;
        this.FindControl<Button>("ToggleInspectorButton")!.Classes.Set("selected", inspector);
        this.FindControl<Button>("ToggleThumbnailsButton")!.Classes.Set("selected", showThumbnails);
    }
    private static object[] LayoutObjects(string layout)
    {
        static object Text(string text, double x, double y, double width, double height, double size, bool bold = false) => new { id = Guid.NewGuid().ToString(), kind = "text", text, x, y, width, height, fill = "#243249", font_size = size, bold };
        return layout switch
        {
            "title" => [Text("Le titre de votre présentation", 72, 150, 820, 150, 52, true), Text("Un sous-titre pour donner le contexte", 76, 324, 800, 80, 26)],
            "content" => [Text("Le titre de la diapositive", 56, 40, 840, 90, 40, true), Text("Votre idée principale\n\nLes éléments à retenir\n\nLa prochaine étape", 60, 160, 820, 310, 28)],
            "columns" => [Text("Deux idées à mettre en perspective", 56, 40, 840, 90, 38, true), Text("Premier point\n\nDéveloppez votre première idée ici.", 60, 165, 390, 310, 28), Text("Deuxième point\n\nPrésentez un autre angle ou une comparaison.", 500, 165, 390, 310, 28)],
            _ => throw new ArgumentException("Disposition inconnue.", nameof(layout))
        };
    }
    public async Task AddLayoutAsync(string layout)
    {
        await FlushAsync();
        var id = Guid.NewGuid().ToString(); var objects = LayoutObjects(layout);
        await Enqueue(() => Document.ExecuteAsync("AddSlide", new { slide = new { id, objects } }));
        if (LastError is null) { slideId = id; activeObject = null; Surface.Select(null); Render(); }
    }
    private void LoadTextStyle(JsonNode? item)
    {
        this.FindControl<NumericUpDown>("ObjectFontSize")!.Value = (decimal)(item?["font_size"]?.GetValue<double>() ?? 28);
        this.FindControl<Button>("ObjectBoldButton")!.Classes.Set("selected", item?["bold"]?.GetValue<bool>() == true);
    }
}
