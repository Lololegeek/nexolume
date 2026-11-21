using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Nexolume.Desktop.Platform;

namespace Nexolume.Desktop.Editors.Writer.Views;

public partial class WriterView
{
    private static readonly string[] RecommendedFonts =
    [
        "Aptos", "Calibri", "Cambria", "Candara", "Constantia", "Corbel", "Segoe UI", "Arial", "Arial Narrow", "Verdana", "Tahoma", "Trebuchet MS",
        "Georgia", "Times New Roman", "Palatino Linotype", "Book Antiqua", "Garamond", "Bookman Old Style", "Century Gothic", "Franklin Gothic Medium",
        "Gill Sans MT", "Consolas", "Courier New", "Lucida Console", "Cascadia Code", "Comic Sans MS", "Impact", "Segoe Print", "Segoe Script", "Ink Free"
    ];
    // Fonts whose glyphs are pictograms: their name is written in the interface font, with a sample beside it.
    private static readonly string[] SymbolFonts = ["Wingdings", "Wingdings 2", "Wingdings 3", "Webdings", "Symbol", "Marlett", "MT Extra", "Bookshelf Symbol 7", "Segoe MDL2 Assets", "Segoe Fluent Icons", "HoloLens MDL2 Assets"];
    // List entries that are not fonts: a section title, or the browser's "show device fonts" action.
    private const char HeaderMark = '\u0001', DeviceFontsMark = '\u0002';
    private const string DeviceFontsEntry = "\u0002Afficher les polices de cet appareil…";
    private readonly List<string> recentFonts = [];
    private List<string> installedFonts = [];
    private string? lastChosenFont;

    private static bool IsFontMarker(string? item) => item is { Length: > 0 } && (item[0] == HeaderMark || item[0] == DeviceFontsMark);

    private void SetupFonts()
    {
        installedFonts = InstalledFonts();
        var box = this.FindControl<ComboBox>("FontFamilyBox")!;
        box.Width = 200; box.MaxDropDownHeight = 480; box.Classes.Add("fontPicker");
        box.ItemTemplate = FontRowTemplate();
        box.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        // Titles and notes cannot be picked or reached with the arrow keys.
        box.ContainerPrepared += (_, e) => { var item = e.Container.DataContext as string; e.Container.IsEnabled = item is null || item[0] != HeaderMark; e.Container.Classes.Set("fontHeader", item is { Length: > 0 } && item[0] == HeaderMark); };
        box.SelectionChanged += async (_, _) =>
        {
            if (box.SelectedItem is not string item) return;
            if (!IsFontMarker(item)) { lastChosenFont = item; return; }
            // Put the document's font back first, so the list never shows an entry that is not a font.
            var previous = lastChosenFont;
            Dispatcher.UIThread.Post(() => { var was = updatingFont; updatingFont = true; try { box.SelectedItem = previous; } finally { updatingFont = was; } });
            if (item[0] != DeviceFontsMark) return;
            SetStatus("Autorisez l’accès aux polices dans la fenêtre du navigateur…");
            SetStatus(await DeviceFonts.RequestAsync() ? $"{DeviceFonts.Families.Count} polices de l’appareil disponibles." : "Le navigateur n’a pas donné accès aux polices de l’appareil.");
        };
        AttachedToVisualTree += (_, _) => DeviceFonts.Changed += OnDeviceFontsChanged;
        DetachedFromVisualTree += (_, _) => DeviceFonts.Changed -= OnDeviceFontsChanged;
        RefreshFontList(null);
    }

    private static List<string> InstalledFonts()
    {
        IEnumerable<string> names = AppPlatform.IsBrowser ? DeviceFonts.Families : FontManager.Current.SystemFonts.Select(f => f.Name);
        return names.Where(n => !string.IsNullOrWhiteSpace(n) && !n.StartsWith('@')).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // Device fonts were listed or a family's files were read (browser): rebuild the list and lay the text out again.
    private void OnDeviceFontsChanged()
    {
        installedFonts = InstalledFonts();
        var box = this.FindControl<ComboBox>("FontFamilyBox")!;
        box.ItemTemplate = FontRowTemplate();
        RefreshFontList(lastChosenFont ?? box.SelectedItem as string);
        foreach (var paragraph in boxes.Values) { var family = paragraph.FontFamily; paragraph.FontFamily = FontFamily.Default; paragraph.FontFamily = family; paragraph.InvalidateVisual(); }
    }

    private IDataTemplate FontRowTemplate() => new FuncDataTemplate<string>((_, _) =>
    {
        // Fixed-height rows: fonts with tall metrics would otherwise resize rows while scrolling and make the list jump.
        var name = new TextBlock { FontSize = 14.5, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var sample = new TextBlock { FontSize = 15, MaxLines = 1, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75, IsVisible = false };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(name); grid.Children.Add(sample); Grid.SetColumn(sample, 1);
        var row = new Border { Height = 30, MinWidth = 260, ClipToBounds = true, Padding = new Thickness(2, 0), Child = grid };
        row.DataContextChanged += (_, _) =>
        {
            var item = row.DataContext as string ?? "";
            var header = item.Length > 0 && item[0] == HeaderMark; var action = item.Length > 0 && item[0] == DeviceFontsMark;
            var symbol = SymbolFonts.Contains(item, StringComparer.OrdinalIgnoreCase);
            name.Text = header || action ? item[1..] : item;
            name.ClearValue(TextBlock.ForegroundProperty);
            name.FontFamily = header || action || symbol ? FontFamily.Default : SafeFamily(item);
            name.FontSize = header ? 11 : 14.5; name.FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal;
            name.Opacity = header ? 0.6 : 1;
            if (action && this.TryFindResource("OfficeAccent", ActualThemeVariant, out var accent) && accent is IBrush brush) name.Foreground = brush;
            row.Height = header ? 26 : 30; row.Padding = header ? new Thickness(2, 6, 2, 0) : new Thickness(2, 0);
            name.VerticalAlignment = header ? VerticalAlignment.Bottom : VerticalAlignment.Center;
            sample.IsVisible = symbol; sample.Text = symbol ? "AaBbCc" : ""; sample.FontFamily = SafeFamily(item);
        };
        return row;
    });

    private static FontFamily SafeFamily(string? name) { try { return new FontFamily(name ?? "Segoe UI"); } catch { return new FontFamily("Segoe UI"); } }

    // Recent fonts first, then the recommended ones that are installed, then every other installed family; the document's own font is always present.
    public List<string> FontChoices(string? current)
    {
        var list = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Section(string title, IEnumerable<string?> names)
        {
            var fonts = names.Where(n => !string.IsNullOrWhiteSpace(n) && !IsFontMarker(n) && seen.Add(n!)).Select(n => n!).ToList();
            if (fonts.Count == 0) return;
            list.Add(HeaderMark + title); list.AddRange(fonts);
        }
        Section("Polices récentes", recentFonts);
        Section("Police du document", [current]);
        Section("Polices recommandées", RecommendedFonts.Where(n => n == "Aptos" || installedFonts.Contains(n, StringComparer.OrdinalIgnoreCase)));
        Section(AppPlatform.IsBrowser ? "Polices de l’appareil" : "Toutes les polices", installedFonts);
        if (DeviceFonts.CanRequest) list.Add(DeviceFontsEntry);
        else if (AppPlatform.IsBrowser && installedFonts.Count == 0) list.Add(HeaderMark + "Ce navigateur ne donne pas accès aux polices de l’appareil (Chrome ou Edge requis).");
        return list;
    }

    private void RefreshFontList(string? current)
    {
        var box = this.FindControl<ComboBox>("FontFamilyBox")!;
        var was = updatingFont; updatingFont = true;
        try { box.ItemsSource = FontChoices(current); box.SelectedItem = current; if (current is not null) lastChosenFont = current; } finally { updatingFont = was; }
    }

    private void RememberFont(string font) { recentFonts.Remove(font); recentFonts.Insert(0, font); if (recentFonts.Count > 5) recentFonts.RemoveAt(5); }
}
