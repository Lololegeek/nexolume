using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Nexolume.Desktop.DesignSystem.Icons;
namespace Nexolume.Desktop.Home;

public partial class HomeView : UserControl
{
    private string[] recent = [];
    private string recentFilter = "All";
    public event Action<string>? CreateRequested;
    public event Action? OpenRequested;
    public event Action<string>? RecentRequested;
    public HomeView() { AvaloniaXamlLoader.Load(this); foreach (var (name, kind) in new[] { ("CreateWriter", "writer"), ("CreateSheets", "sheets"), ("CreateSlides", "slides") }) this.FindControl<Button>(name)!.Click += (_, _) => CreateRequested?.Invoke(kind); this.FindControl<Button>("OpenDocument")!.Click += (_, _) => OpenRequested?.Invoke(); this.FindControl<TextBox>("RecentSearch")!.TextChanged += (_, _) => RenderRecent(); foreach (var key in new[] { "All", "Writer", "Sheets", "Slides" }) this.FindControl<Button>("Recent" + key)!.Click += (_, _) => { recentFilter = key; foreach (var k in new[] { "All", "Writer", "Sheets", "Slides" }) this.FindControl<Button>("Recent" + k)!.Classes.Set("selected", k == key); RenderRecent(); }; this.FindControl<ComboBox>("RecentSort")!.SelectionChanged += (_, _) => RenderRecent(); ActualThemeVariantChanged += (_, _) => RenderRecent(); RenderRecent(); }
    public void SetRecent(IEnumerable<string> paths) { recent = paths.ToArray(); RenderRecent(); }
    private void RenderRecent()
    {
        var panel = this.FindControl<StackPanel>("RecentPanel")!; panel.Children.Clear(); var query = this.FindControl<TextBox>("RecentSearch")!.Text ?? "";
        var paths = recent.Distinct(StringComparer.OrdinalIgnoreCase).Where(p => p.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        if (this.FindControl<ComboBox>("RecentSort")!.SelectedIndex == 1) paths = paths.OrderBy(p => System.IO.Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase);
        foreach (var path in paths)
        {
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant(); var (kind, color) = ext switch { ".ossheet" => ("Sheets", "#69CCB2"), ".osslides" => ("Slides", "#E9B27E"), _ => ("Writer", "#8AB3FF") };
            if (recentFilter != "All" && kind != recentFilter) continue;
            if (ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light) color = kind switch { "Writer" => "#356AC4", "Sheets" => "#26735F", _ => "#9A5B24" }; var exists = File.Exists(path); var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,130,140") };
            var name = new Grid { ColumnDefinitions = new ColumnDefinitions("32,*") }; name.Children.Add(new OfficeIcon { Kind = kind == "Writer" ? "document" : kind.ToLowerInvariant(), Stroke = Brush.Parse(color), VerticalAlignment = VerticalAlignment.Center });
            var label = new StackPanel { Spacing = 3 }; label.Children.Add(new TextBlock { Text = System.IO.Path.GetFileNameWithoutExtension(path), FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }); label.Children.Add(new TextBlock { Text = System.IO.Path.GetDirectoryName(path), Classes = { "caption" }, TextTrimming = TextTrimming.CharacterEllipsis }); Grid.SetColumn(label, 1); name.Children.Add(label); row.Children.Add(name);
            var type = new TextBlock { Text = ext switch { ".docx" => "Word · DOCX", ".odt" => "OpenDocument", _ => kind }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }; Grid.SetColumn(type, 1); row.Children.Add(type);
            var date = new TextBlock { Text = exists ? File.GetLastWriteTime(path).ToString("dd MMM yyyy, HH:mm") : "Fichier introuvable", VerticalAlignment = VerticalAlignment.Center, Classes = { "caption" } }; Grid.SetColumn(date, 2); row.Children.Add(date);
            var button = new Button { Content = row, Classes = { "recent" }, IsEnabled = exists }; ToolTip.SetTip(button, path); button.Click += (_, _) => RecentRequested?.Invoke(path); panel.Children.Add(button);
        }
        this.FindControl<StackPanel>("EmptyRecent")!.IsVisible = panel.Children.Count == 0;
        this.FindControl<TextBlock>("EmptyTitle")!.Text = query.Length == 0 && recentFilter == "All" ? "Votre travail trouvera sa place ici" : "Aucun document correspondant";
        this.FindControl<TextBlock>("EmptyDescription")!.Text = query.Length == 0 && recentFilter == "All" ? "Créez un document ou ouvrez un fichier pour commencer." : "Essayez un autre nom ou sélectionnez Tous.";
    }
}
