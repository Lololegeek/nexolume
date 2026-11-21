using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nexolume.Desktop.DesignSystem.Icons;

namespace Nexolume.Desktop.DesignSystem.Controls;

/// <summary>
/// Font family picker: a field that shows the current font in its own face, and a panel with a search box,
/// style filters, favourites, sections (recent, document, recommended, all) and a live preview of the font
/// under the pointer or the keyboard highlight.
/// </summary>
public sealed class FontPicker : UserControl
{
    public enum Category { Sans, Serif, Mono, Script, Symbol }

    private sealed record Row(string Name, bool IsHeader = false, int Count = 0);

    public sealed class Catalog
    {
        public IReadOnlyList<string> Recent { get; init; } = [];
        public IReadOnlyList<string> Recommended { get; init; } = [];
        public IReadOnlyList<string> All { get; init; } = [];
        public string AllTitle { get; init; } = "Toutes les polices";
        /// <summary>Optional action shown in the footer (browser: ask for the device fonts).</summary>
        public string? ActionText { get; init; }
        public Func<Task>? Action { get; init; }
        /// <summary>Optional note shown in the footer when there is no action.</summary>
        public string? Note { get; init; }
    }

    private static readonly string[] SymbolNames = ["Wingdings", "Wingdings 2", "Wingdings 3", "Webdings", "Symbol", "Marlett", "MT Extra", "Bookshelf Symbol 7", "Segoe MDL2 Assets", "Segoe Fluent Icons", "HoloLens MDL2 Assets", "Segoe UI Symbol", "Segoe UI Emoji"];
    private static readonly string[] SerifHints = ["serif", "times", "georgia", "cambria", "caladea", "tinos", "garamond", "palatino", "book antiqua", "bookman", "constantia", "baskerville", "didot", "bodoni", "century schoolbook", "rockwell", "sylfaen", "perpetua", "goudy", "cooper", "bell mt", "californian", "calisto", "centaur", "elephant", "footlight", "high tower", "modern no", "niagara", "poor richard", "wide latin", "lora", "merriweather", "playfair", "noto serif", "source serif", "pt serif", "libre baskerville", "cormorant", "crimson", "eb garamond", "liberation serif", "dejavu serif", "minion", "big caslon", "charter", "iowan", "hoefler", "new york", "cochin", "baskerville", "batang", "mincho", "ming", "simsun", "kaiti", "fangsong", "nsimsun"];
    private static readonly string[] MonoHints = ["mono", "consolas", "courier", "cousine", "console", "code", "menlo", "monaco", "fixed", "terminal", "inconsolata", "fira code", "jetbrains", "hack", "ocr a", "typewriter"];
    private static readonly string[] ScriptHints = ["script", "hand", "print", "brush", "comic", "ink free", "chiller", "curlz", "forte", "freestyle", "gigi", "harlow", "jokerman", "kristen", "lucida calligraphy", "magneto", "mistral", "pristina", "rage", "ravie", "vivaldi", "viner", "vladimir", "edwardian", "french script", "kunstler", "palace", "blackadder", "bradley", "gabriola", "segoe print", "segoe script", "marker", "chalk", "pacifico", "dancing", "caveat", "lobster", "satisfy", "great vibes", "kalam", "snell", "zapfino", "apple chancery", "calligraph", "old english", "parchment", "papyrus"];

    public static Category Classify(string name)
    {
        var n = name.ToLowerInvariant();
        if (SymbolNames.Contains(name, StringComparer.OrdinalIgnoreCase) || n.Contains("dings") || n.Contains("symbol") || n.Contains("icons") || n.Contains("mdl2") || n.Contains("emoji")) return Category.Symbol;
        if (MonoHints.Any(n.Contains)) return Category.Mono;
        if (ScriptHints.Any(n.Contains)) return Category.Script;
        if (n.Contains("sans")) return Category.Sans;
        if (SerifHints.Any(n.Contains)) return Category.Serif;
        return Category.Sans;
    }

    private static string CategoryLabel(Category? c) => c switch
    {
        Category.Sans => L.T("Sans empattement"), Category.Serif => L.T("Avec empattement"), Category.Mono => L.T("Chasse fixe"),
        Category.Script => L.T("Manuscrite"), Category.Symbol => L.T("Symboles"), _ => L.T("Toutes"),
    };

    private static string ChipLabel(Category? c) => c switch
    {
        Category.Sans => L.T("Sans serif"), Category.Serif => L.T("Serif"), Category.Mono => L.T("Mono"),
        Category.Script => L.T("Manuscrite"), Category.Symbol => L.T("Symboles"), _ => L.T("Toutes"),
    };

    public static readonly StyledProperty<string?> SelectedFontProperty = AvaloniaProperty.Register<FontPicker, string?>(nameof(SelectedFont));
    public string? SelectedFont { get => GetValue(SelectedFontProperty); set => SetValue(SelectedFontProperty, value); }

    /// <summary>Raised when the user picks a font (not when <see cref="SelectedFont"/> is set from code).</summary>
    public event Action<string>? FontChosen;
    /// <summary>Called each time the panel opens, so the lists are current.</summary>
    public Func<Catalog>? CatalogProvider { get; set; }
    /// <summary>Favourite fonts, read and written by the host (kept in the preferences).</summary>
    public Func<IReadOnlyCollection<string>>? Favorites { get; set; }
    public Action<string, bool>? SetFavorite { get; set; }
    public string PreviewText { get; set; } = "Portez ce vieux whisky au juge blond qui fume.";

    private readonly Button field;
    private readonly TextBlock fieldText;
    private readonly Flyout flyout;
    private readonly TextBox search;
    private readonly ListBox list;
    private readonly TextBlock previewName, previewMeta, previewSample, previewDigits, countText;
    private readonly Border previewBorder;
    private readonly WrapPanel chips;
    private readonly Button actionButton;
    private readonly TextBlock noteText;
    private readonly TextBlock emptyText;
    private Category? filter;
    private bool favoritesOnly;
    private Catalog catalog = new();
    private List<Row> rows = [];
    private bool moving;

    public FontPicker()
    {
        fieldText = new TextBlock { FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
        var chevron = new OfficeIcon { Kind = "chevron", Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        Tint(chevron, "OfficeMuted");
        var fieldGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        fieldGrid.Children.Add(fieldText); fieldGrid.Children.Add(chevron); Grid.SetColumn(chevron, 1);
        field = new Button { Content = fieldGrid, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center };
        field.Classes.Add("fontPickerField");
        Content = field;

        // Search
        var searchIcon = new OfficeIcon { Kind = "search", Width = 15, Height = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0), IsHitTestVisible = false };
        Tint(searchIcon, "OfficeMuted");
        search = new TextBox { Watermark = L.T("Rechercher une police"), BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(4, 8, 10, 8), VerticalContentAlignment = VerticalAlignment.Center, FontSize = 14 };
        search.Classes.Add("fontSearch");
        var searchGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        searchGrid.Children.Add(searchIcon); searchGrid.Children.Add(search); Grid.SetColumn(search, 1);
        var searchBox = new Border { Child = searchGrid, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(12, 12, 12, 8) };
        searchBox.Classes.Add("fontSearchBox");

        // Filters
        chips = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 6, 6) };
        AddChip("★ " + L.T("Favoris"), () => { favoritesOnly = !favoritesOnly; }, () => favoritesOnly);
        foreach (var c in new Category?[] { null, Category.Sans, Category.Serif, Category.Mono, Category.Script, Category.Symbol })
            AddChip(ChipLabel(c), () => { filter = c; }, () => filter == c);
        var chipScroll = chips;

        // List
        list = new ListBox { ItemTemplate = RowTemplate(), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(6, 2, 6, 6), SelectionMode = SelectionMode.Single };
        list.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        list.Classes.Add("fontList");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.ContainerPrepared += (_, e) =>
        {
            var row = e.Container.DataContext as Row;
            e.Container.IsEnabled = row is { IsHeader: false };
            e.Container.Classes.Set("fontHeader", row is { IsHeader: true });
            e.Container.Classes.Set("current", row is { IsHeader: false } && string.Equals(row.Name, SelectedFont, StringComparison.OrdinalIgnoreCase));
        };
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is Row { IsHeader: false } r) ShowPreview(r.Name); };
        emptyText = new TextBlock { Text = L.T("Aucune police ne correspond."), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Opacity = 0.7, Margin = new Thickness(0, 24) };
        var listHost = new Grid { Children = { list, emptyText } };

        // Preview
        previewName = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold };
        previewMeta = new TextBlock { FontSize = 11, Opacity = 0.65, HorizontalAlignment = HorizontalAlignment.Right };
        var previewHead = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        previewHead.Children.Add(previewName); previewHead.Children.Add(previewMeta); Grid.SetColumn(previewMeta, 1);
        previewSample = new TextBlock { FontSize = 22, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 2), LineHeight = 28 };
        previewDigits = new TextBlock { FontSize = 14, Opacity = 0.7, Text = "AaBbCcDd 0123456789 & ? ! « »", MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
        previewBorder = new Border { Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(12, 4, 12, 0), CornerRadius = new CornerRadius(8), Height = 112, Child = new StackPanel { Children = { previewHead, previewSample, previewDigits } } };
        previewBorder.Classes.Add("fontPreview");

        // Footer
        countText = new TextBlock { FontSize = 11, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center };
        actionButton = new Button { Padding = new Thickness(10, 4), FontSize = 12, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Right };
        actionButton.Classes.Add("fontAction");
        actionButton.Click += async (_, _) => { if (catalog.Action is { } act) { flyout?.Hide(); await act(); } };
        noteText = new TextBlock { FontSize = 11, Opacity = 0.65, IsVisible = false, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(14, 8, 12, 10) };
        footer.Children.Add(countText); footer.Children.Add(actionButton); footer.Children.Add(noteText);
        Grid.SetColumn(actionButton, 1); Grid.SetColumn(noteText, 1);

        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"), Width = 400, Height = 560 };
        panel.Children.Add(searchBox); panel.Children.Add(chipScroll); panel.Children.Add(listHost); panel.Children.Add(previewBorder); panel.Children.Add(footer);
        Grid.SetRow(chipScroll, 1); Grid.SetRow(listHost, 2); Grid.SetRow(previewBorder, 3); Grid.SetRow(footer, 4);

        flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft, FlyoutPresenterClasses = { "fontPicker" } };
        field.Flyout = flyout;
        flyout.Opening += (_, _) => OnOpening();
        flyout.Opened += (_, _) => Dispatcher.UIThread.Post(() => { search.Focus(); ScrollToCurrent(); }, DispatcherPriority.Background);
        flyout.Closed += (_, _) => field.Classes.Set("open", false);

        search.TextChanged += (_, _) => Rebuild(keepHighlight: false);
        search.AddHandler(KeyDownEvent, OnSearchKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        list.AddHandler(KeyDownEvent, OnSearchKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        list.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(true) is { } b && b.Classes.Contains("fontStar")) return;
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(true)?.DataContext is Row { IsHeader: false } r) Choose(r.Name);
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);

        UpdateField();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedFontProperty) UpdateField();
    }

    /// <summary>Font files arrived (browser): draw the field and the open list again with the real faces.</summary>
    public void RefreshFaces()
    {
        UpdateField();
        if (flyout.IsOpen) { catalog = CatalogProvider?.Invoke() ?? catalog; Rebuild(keepHighlight: true); }
    }

    private void UpdateField()
    {
        var name = SelectedFont ?? "";
        fieldText.Text = name;
        fieldText.FontFamily = Classify(name) == Category.Symbol || name.Length == 0 ? FontFamily.Default : SafeFamily(name);
        ToolTip.SetTip(field, name.Length > 0 ? L.T("Police") + " : " + name : L.T("Police"));
    }

    private void OnOpening()
    {
        field.Classes.Set("open", true);
        catalog = CatalogProvider?.Invoke() ?? new Catalog();
        search.Text = "";
        actionButton.IsVisible = catalog.Action is not null && catalog.ActionText is not null;
        actionButton.Content = catalog.ActionText;
        noteText.IsVisible = !actionButton.IsVisible && catalog.Note is not null;
        noteText.Text = catalog.Note;
        Rebuild(keepHighlight: false);
        ShowPreview(SelectedFont ?? rows.FirstOrDefault(r => !r.IsHeader)?.Name);
    }

    private void AddChip(string text, Action toggle, Func<bool> isOn)
    {
        var chip = new Button { Content = text, Padding = new Thickness(10, 3), FontSize = 12, Focusable = false, Margin = new Thickness(0, 0, 6, 6) };
        chip.Classes.Add("fontChip");
        chip.Click += (_, _) => { toggle(); RefreshChips(); Rebuild(keepHighlight: false); };
        chip.Tag = isOn;
        chips.Children.Add(chip);
        chip.Classes.Set("on", isOn());
    }

    private void RefreshChips() { foreach (var c in chips.Children.OfType<Button>()) if (c.Tag is Func<bool> on) c.Classes.Set("on", on()); }

    private bool Matches(string name, string query)
    {
        if (filter is { } f && Classify(name) != f) return false;
        if (favoritesOnly && !(Favorites?.Invoke().Contains(name, StringComparer.OrdinalIgnoreCase) ?? false)) return false;
        return query.Length == 0 || name.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void Rebuild(bool keepHighlight)
    {
        var keep = keepHighlight ? (list.SelectedItem as Row)?.Name : null;
        var query = (search.Text ?? "").Trim();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Row>();
        void Section(string title, IEnumerable<string?> names)
        {
            var fonts = names.Where(n => !string.IsNullOrWhiteSpace(n) && Matches(n!, query) && seen.Add(n!)).Select(n => new Row(n!)).ToList();
            if (fonts.Count == 0) return;
            result.Add(new Row(title, true, fonts.Count)); result.AddRange(fonts);
        }
        var favorites = Favorites?.Invoke() ?? [];
        if (query.Length == 0)
        {
            Section(L.T("Favoris"), favorites.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase));
            Section(L.T("Police du document"), [SelectedFont]);
            Section(L.T("Récentes"), catalog.Recent);
            Section(L.T("Recommandées"), catalog.Recommended);
            Section(catalog.AllTitle, catalog.All);
        }
        else
        {
            // While searching: names that start with the query first, then the others; one flat list.
            var all = catalog.All.Concat(catalog.Recommended).Concat(favorites).Append(SelectedFont).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).Distinct(StringComparer.OrdinalIgnoreCase);
            Section(L.T("Résultats"), all.OrderBy(n => n.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1).ThenBy(n => n, StringComparer.CurrentCultureIgnoreCase));
        }
        rows = result;
        list.ItemsSource = rows;
        emptyText.IsVisible = rows.Count == 0;
        var total = catalog.All.Concat(catalog.Recommended).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var shown = rows.Count(r => !r.IsHeader);
        countText.Text = query.Length > 0 || filter is not null || favoritesOnly ? L.F("{0} sur {1} polices", shown, total) : L.F("{0} polices", total);
        var target = keep is not null ? rows.FindIndex(r => !r.IsHeader && r.Name == keep) : -1;
        if (target < 0 && query.Length > 0) target = rows.FindIndex(r => !r.IsHeader);
        if (target < 0 && SelectedFont is { } cur) target = rows.FindIndex(r => !r.IsHeader && string.Equals(r.Name, cur, StringComparison.OrdinalIgnoreCase));
        moving = true;
        try { list.SelectedIndex = target; } finally { moving = false; }
        if (target >= 0) { list.ScrollIntoView(target); ShowPreview(rows[target].Name); }
    }

    private void ScrollToCurrent()
    {
        if (list.SelectedIndex is var i and >= 0)
        {
            // Keep the section title above the current font visible.
            list.ScrollIntoView(Math.Max(0, i - 1));
            list.ScrollIntoView(i);
        }
    }

    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: Move(+1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
            case Key.PageDown: Move(+10); e.Handled = true; break;
            case Key.PageUp: Move(-10); e.Handled = true; break;
            case Key.Enter:
                if (list.SelectedItem is Row { IsHeader: false } r) Choose(r.Name);
                else if (search.Text is { Length: > 0 } typed) Choose(typed.Trim());
                e.Handled = true; break;
            case Key.Escape: flyout.Hide(); field.Focus(); e.Handled = true; break;
            default:
                // Typing while the list has focus goes to the search box.
                if (sender == list && e.KeySymbol is { Length: 1 } s && !char.IsControl(s[0])) { search.Focus(); search.Text += s; search.CaretIndex = search.Text.Length; e.Handled = true; }
                break;
        }
    }

    private void Move(int step)
    {
        if (rows.Count == 0) return;
        var i = list.SelectedIndex;
        var direction = Math.Sign(step);
        var target = i < 0 ? (direction > 0 ? -1 : rows.Count) : i;
        var remaining = Math.Abs(step);
        var last = i;
        while (remaining > 0)
        {
            target += direction;
            if (target < 0 || target >= rows.Count) break;
            if (rows[target].IsHeader) continue;
            last = target; remaining--;
        }
        if (last < 0 || last == i) return;
        list.SelectedIndex = last;
        list.ScrollIntoView(last);
    }

    private void Choose(string name)
    {
        flyout.Hide();
        SelectedFont = name;
        FontChosen?.Invoke(name);
    }

    private void ShowPreview(string? name)
    {
        if (moving && name is null) return;
        if (string.IsNullOrEmpty(name)) { previewName.Text = ""; previewMeta.Text = ""; previewSample.Text = ""; return; }
        var category = Classify(name);
        var family = SafeFamily(name);
        previewName.Text = name;
        previewMeta.Text = CategoryLabel(category);
        previewSample.FontFamily = family; previewDigits.FontFamily = family;
        previewSample.Text = category == Category.Symbol ? "abcdefghijklmnopqrstuvwxyz" : PreviewText;
    }

    private IDataTemplate RowTemplate() => new FuncDataTemplate<Row>((_, _) =>
    {
        var name = new TextBlock { FontSize = 15, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var sample = new TextBlock { FontSize = 15, MaxLines = 1, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, IsVisible = false };
        var check = new OfficeIcon { Kind = "check", Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), IsVisible = false };
        Tint(check, "OfficeAccent");
        var star = new Button { Content = "☆", Padding = new Thickness(6, 0), FontSize = 14, Focusable = false, VerticalAlignment = VerticalAlignment.Center };
        star.Classes.Add("fontStar");
        var headerText = new TextBlock { FontSize = 10.5, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.8, VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0.6 };
        var headerCount = new TextBlock { FontSize = 10.5, VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0.45, HorizontalAlignment = HorizontalAlignment.Right };
        var fontGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        fontGrid.Children.Add(check); fontGrid.Children.Add(name); fontGrid.Children.Add(sample); fontGrid.Children.Add(star);
        Grid.SetColumn(name, 1); Grid.SetColumn(sample, 2); Grid.SetColumn(star, 3);
        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), IsVisible = false, Margin = new Thickness(0, 0, 4, 4) };
        headerGrid.Children.Add(headerText); headerGrid.Children.Add(headerCount); Grid.SetColumn(headerCount, 1);
        // Fixed-height rows: fonts with tall metrics would otherwise resize rows while scrolling and make the list jump.
        var root = new Border { Height = 34, ClipToBounds = true, Padding = new Thickness(8, 0, 2, 0), Background = Brushes.Transparent, Child = new Panel { Children = { fontGrid, headerGrid } } };
        Row? current = null;
        root.PointerEntered += (_, _) => { if (current is { IsHeader: false } r) ShowPreview(r.Name); };
        root.PointerExited += (_, _) => { if (list.SelectedItem is Row { IsHeader: false } r) ShowPreview(r.Name); };
        star.Click += (_, e) =>
        {
            e.Handled = true;
            if (current is not { IsHeader: false } r || SetFavorite is null) return;
            var on = !(Favorites?.Invoke().Contains(r.Name, StringComparer.OrdinalIgnoreCase) ?? false);
            SetFavorite(r.Name, on);
            Rebuild(keepHighlight: true);
        };
        root.DataContextChanged += (_, _) =>
        {
            current = root.DataContext as Row;
            if (current is null) return;
            var header = current.IsHeader;
            fontGrid.IsVisible = !header; headerGrid.IsVisible = header;
            root.Height = header ? 30 : 34;
            if (header) { headerText.Text = current.Name.ToUpper(System.Globalization.CultureInfo.CurrentCulture); headerCount.Text = current.Count.ToString(System.Globalization.CultureInfo.CurrentCulture); return; }
            var symbol = Classify(current.Name) == Category.Symbol;
            name.Text = current.Name;
            name.FontFamily = symbol ? FontFamily.Default : SafeFamily(current.Name);
            sample.IsVisible = symbol; sample.Text = symbol ? "AaBbCc" : ""; sample.FontFamily = SafeFamily(current.Name);
            var isCurrent = string.Equals(current.Name, SelectedFont, StringComparison.OrdinalIgnoreCase);
            check.IsVisible = isCurrent;
            name.FontWeight = isCurrent ? FontWeight.SemiBold : FontWeight.Normal;
            var fav = Favorites?.Invoke().Contains(current.Name, StringComparer.OrdinalIgnoreCase) ?? false;
            star.Content = fav ? "★" : "☆";
            star.Classes.Set("on", fav);
            star.IsVisible = SetFavorite is not null;
            ToolTip.SetTip(star, fav ? L.T("Retirer des favoris") : L.T("Ajouter aux favoris"));
        };
        return root;
    });

    private static void Tint(OfficeIcon icon, string resource) => icon.Bind(OfficeIcon.StrokeProperty, icon.GetResourceObservable(resource));

    private static FontFamily SafeFamily(string? name) { try { return new FontFamily(string.IsNullOrWhiteSpace(name) ? "Segoe UI" : name); } catch { return FontFamily.Default; } }
}
