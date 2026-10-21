using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;

namespace Nexolume.Desktop.Editors.Writer.Views;

public partial class WriterView
{
    private static readonly string[] RecommendedFonts =
    [
        "Aptos", "Calibri", "Cambria", "Candara", "Constantia", "Corbel", "Segoe UI", "Arial", "Arial Narrow", "Verdana", "Tahoma", "Trebuchet MS",
        "Georgia", "Times New Roman", "Palatino Linotype", "Book Antiqua", "Garamond", "Bookman Old Style", "Century Gothic", "Franklin Gothic Medium",
        "Gill Sans MT", "Consolas", "Courier New", "Lucida Console", "Cascadia Code", "Comic Sans MS", "Impact", "Segoe Print", "Segoe Script", "Ink Free"
    ];
    private readonly List<string> recentFonts = [];
    private List<string> installedFonts = [];

    private void SetupFonts()
    {
        installedFonts = FontManager.Current.SystemFonts.Select(f => f.Name).Where(n => !string.IsNullOrWhiteSpace(n) && !n.StartsWith('@')).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        var box = this.FindControl<ComboBox>("FontFamilyBox")!;
        box.Width = 200; box.MaxDropDownHeight = 460;
        // Fixed-height rows: fonts with tall metrics would otherwise resize rows while scrolling and make the list jump.
        box.ItemTemplate = new FuncDataTemplate<string>((name, _) => new Border
        {
            Height = 30, ClipToBounds = true, Padding = new Avalonia.Thickness(4, 0),
            Child = new TextBlock { Text = name, FontFamily = SafeFamily(name), FontSize = 15, LineHeight = 20, LineSpacing = 0, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }
        });
        box.ItemsPanel = new FuncTemplate<Avalonia.Controls.Panel?>(() => new VirtualizingStackPanel());
        RefreshFontList(null);
    }

    private static FontFamily SafeFamily(string? name) { try { return new FontFamily(name ?? "Segoe UI"); } catch { return new FontFamily("Segoe UI"); } }

    // Recent fonts first, then the recommended ones that are installed, then every other installed family; the document's own font is always present.
    public List<string> FontChoices(string? current)
    {
        var list = new List<string>();
        void Add(string? name) { if (!string.IsNullOrWhiteSpace(name) && !list.Contains(name, StringComparer.OrdinalIgnoreCase)) list.Add(name); }
        foreach (var name in recentFonts) Add(name);
        Add(current);
        foreach (var name in RecommendedFonts) if (installedFonts.Contains(name, StringComparer.OrdinalIgnoreCase) || name == "Aptos") Add(name);
        foreach (var name in installedFonts) Add(name);
        return list;
    }

    private void RefreshFontList(string? current)
    {
        var box = this.FindControl<ComboBox>("FontFamilyBox")!;
        var was = updatingFont; updatingFont = true;
        try { box.ItemsSource = FontChoices(current); box.SelectedItem = current; } finally { updatingFont = was; }
    }

    private void RememberFont(string font) { recentFonts.Remove(font); recentFonts.Insert(0, font); if (recentFonts.Count > 5) recentFonts.RemoveAt(5); }
}
