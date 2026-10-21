using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using OfficeBridge.Services;
using Nexolume.Desktop.Editors.Slides.Controls;
using System.Globalization;
using System.Text.Json.Nodes;
namespace Nexolume.Desktop.Editors.Slides.Views;

public partial class SlidesView : EditorView
{
    public SlideSurface Surface { get; }
    private readonly ListBox list;
    private string slideId;
    private string? activeObject;
    private bool rendering, draft;
    public override bool HasDraft => draft || notesDraft;
    private JsonArray Slides => Document.Snapshot["content"]!["slides"]!.AsArray();
    private JsonNode Slide => Slides.First(s => s!["id"]!.GetValue<string>() == slideId)!;
    public SlidesView(DocumentSession document) : base(document)
    {
        AvaloniaXamlLoader.Load(this); Surface = this.FindControl<SlideSurface>("SlideCanvas")!; list = this.FindControl<ListBox>("SlideList")!; slideId = Slides[0]!["id"]!.GetValue<string>();
        this.FindControl<Button>("AddSlideButton")!.Click += async (_, _) => await AddSlideAsync();
        this.FindControl<Button>("DeleteSlideButton")!.Click += async (_, _) => { await FlushAsync(); await Enqueue(() => Document.ExecuteAsync("DeleteSlide", new { id = slideId })); };
        foreach (var (name, kind) in new[] { ("AddTextButton", "text"), ("AddRectangleButton", "rectangle"), ("AddEllipseButton", "ellipse") }) this.FindControl<Button>(name)!.Click += async (_, _) => await AddObjectAsync(kind);
        this.FindControl<Button>("DeleteObjectButton")!.Click += async (_, _) => { await FlushAsync(); var id = activeObject; if (id is not null) await Enqueue(() => Document.ExecuteAsync("DeleteObject", new { slide = slideId, id })); };
        SetupImages();
        this.FindControl<Button>("ApplyObjectButton")!.Click += async (_, _) => await FlushAsync();
        list.SelectionChanged += async (_, _) => { if (rendering || list.SelectedItem is not ListBoxItem item) return; var next = (string)item.Tag!; await FlushAsync(); slideId = next; activeObject = null; Surface.Select(null); Render(false); };
        Surface.SelectionChanged += async () => { await FlushAsync(); activeObject = Surface.Selected; LoadProperties(); };
        Surface.TransformCommitted += async (id, rect) => { await FlushAsync(); await Enqueue(() => Document.ExecuteAsync("UpdateObject", new { slide = slideId, id, x = rect.X, y = rect.Y, width = rect.Width, height = rect.Height })); };
        foreach (var name in new[] { "ObjectText", "ObjectX", "ObjectY", "ObjectWidth", "ObjectHeight", "ObjectFill" }) this.FindControl<TextBox>(name)!.TextChanged += (_, _) => { if (!rendering) { var o = Surface.Objects.FirstOrDefault(o => o!["id"]!.GetValue<string>() == activeObject); draft = o is not null && Fields.Any(f => (this.FindControl<TextBox>(f.Name)!.Text ?? "") != PropertyDisplay(o, f.Key)); DraftUpdated(); } };
        foreach (var (name, color) in new[] { ("BlueFill", "#285CB3"), ("GreenFill", "#2D806C"), ("OrangeFill", "#C97F4A"), ("SlateFill", "#48556D") }) this.FindControl<Button>(name)!.Click += async (_, _) => { await FlushAsync(); if (activeObject is not null) await Enqueue(() => Document.ExecuteAsync("UpdateObject", new { slide = slideId, id = activeObject, fill = color })); };
        SetupOfficeTools(); SetupSlidesProductivity(); SetupSlideDesign(); document.Changed += OnChanged; Render();
    }
    private void OnChanged(object? sender, EventArgs e) => Render();
    public async Task AddSlideAsync() { await FlushAsync(); var id = Guid.NewGuid().ToString(); await Enqueue(() => Document.ExecuteAsync("AddSlide", new { id })); slideId = id; activeObject = null; Render(); }
    public async Task AddObjectAsync(string kind) { await FlushAsync(); var id = Guid.NewGuid().ToString(); var count = Slide["objects"]!.AsArray().Count; var line = kind == "line"; await Enqueue(() => Document.ExecuteAsync("AddObject", new { slide = slideId, @object = new { id, kind, text = kind == "text" ? "Votre texte" : "", x = 80 + count * 35, y = 70 + count * 35, width = kind == "text" ? 450 : line ? 300 : 240, height = kind == "text" ? 100 : line ? 10 : 160, fill = kind == "text" || line ? "#243249" : kind is "ellipse" or "diamond" ? "#C97F4A" : kind == "triangle" ? "#2D806C" : "#285CB3" } })); activeObject = id; Surface.Select(id); LoadProperties(); }
    private void Render(bool rebuildThumbnails = true)
    {
        rendering = true;
        try
        {
            if (!Slides.Any(s => s!["id"]!.GetValue<string>() == slideId)) { slideId = Slides[0]!["id"]!.GetValue<string>(); activeObject = null; }
            var selectedIndex = Slides.IndexOf(Slide); if (rebuildThumbnails)
            {
                list.SelectedIndex = -1; list.Items.Clear();
                for (int i = 0; i < Slides.Count; i++) { var s = Slides[i]!; var id = s["id"]!.GetValue<string>(); if (id == slideId) selectedIndex = i; var thumbnail = new SlideSurface { Objects = s["objects"]!.AsArray(), Background = s["background"]?.GetValue<string>() ?? "#FFFFFF", IsHitTestVisible = false }; var panel = new StackPanel { Spacing = 5 }; panel.Children.Add(new TextBlock { Text = "Diapositive " + (i + 1), FontSize = 11 }); panel.Children.Add(new Border { BorderBrush = Brush.Parse("#97A6BC"), BorderThickness = new Thickness(1), Child = new Viewbox { Width = 145, Height = 82, Child = thumbnail } }); list.Items.Add(new ListBoxItem { Tag = id, Content = panel, Padding = new Thickness(6) }); }
                list.SelectedIndex = selectedIndex;
            }
            Surface.Objects = Slide["objects"]!.AsArray(); Surface.Background = Slide["background"]?.GetValue<string>() ?? "#FFFFFF"; LoadSlideProperties(); Surface.InvalidateVisual();
            if (activeObject is not null && !Surface.Objects.Any(o => o!["id"]!.GetValue<string>() == activeObject)) { activeObject = null; Surface.Select(null); }
            this.FindControl<Button>("DeleteSlideButton")!.IsEnabled = Slides.Count > 1; UpdateOfficeActions(); this.FindControl<TextBlock>("SlideStatus")!.Text = $"Diapositive {selectedIndex + 1} sur {Slides.Count}  ·  {Surface.Objects.Count} objet{(Surface.Objects.Count != 1 ? "s" : "")}";
            if (!draft) LoadProperties();
        }
        finally { rendering = false; }
    }
    private static string PropertyDisplay(JsonNode? item, string key) => item is null ? "" : new[] { "x", "y", "width", "height" }.Contains(key) ? item[key]!.GetValue<double>().ToString("0.##", CultureInfo.InvariantCulture) : item[key]?.ToString() ?? "";
    private static readonly (string Name, string Key)[] Fields = [("ObjectText", "text"), ("ObjectX", "x"), ("ObjectY", "y"), ("ObjectWidth", "width"), ("ObjectHeight", "height"), ("ObjectFill", "fill")];
    private void LoadProperties()
    {
        var was = rendering; rendering = true; try
        {
            var o = Surface.Objects.FirstOrDefault(o => o!["id"]!.GetValue<string>() == activeObject); this.FindControl<StackPanel>("PropertiesPanel")!.IsVisible = o is not null;
            this.FindControl<StackPanel>("ObjectEmptyState")!.IsVisible = o is null;
            this.FindControl<TextBlock>("ObjectKindLabel")!.Text = o?["kind"]?.GetValue<string>() switch { "text" => "Zone de texte", "rectangle" => "Rectangle", "ellipse" => "Ellipse", "rounded" => "Rectangle arrondi", "triangle" => "Triangle", "diamond" => "Losange", "line" => "Ligne", "image" => "Image", _ => "Aucun objet sélectionné" };
            this.FindControl<StackPanel>("ObjectTextSection")!.IsVisible = o?["kind"]?.GetValue<string>() == "text";
            this.FindControl<StackPanel>("PropertiesPanel")!.IsEnabled = o is not null; this.FindControl<Button>("DeleteObjectButton")!.IsEnabled = o is not null; UpdateOfficeActions(); foreach (var (name, key) in Fields) this.FindControl<TextBox>(name)!.Text = PropertyDisplay(o, key); LoadTextStyle(o); LoadObjectDesign(o);
        }
        finally { rendering = was; }
    }
    public override async Task FlushAsync()
    {
        if (draft && activeObject is not null)
        {
            draft = false; var id = activeObject; var slide = slideId;
            var fields = Fields.ToDictionary(f => f.Name, f => this.FindControl<TextBox>(f.Name)!.Text ?? "");
            await Enqueue(async () => { double Number(string name) => double.Parse(fields[name], CultureInfo.InvariantCulture); await Document.ExecuteAsync("UpdateObject", new { slide, id, text = fields["ObjectText"], x = Number("ObjectX"), y = Number("ObjectY"), width = Number("ObjectWidth"), height = Number("ObjectHeight"), fill = fields["ObjectFill"] }); }); if (LastError is not null) draft = true; DraftUpdated();
        }
        await FlushNotesAsync();
        await Pending;
    }
    public override void Detach() => Document.Changed -= OnChanged;
}
