using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using OfficeBridge.Services;
using Nexolume.Desktop.Editors;
using Nexolume.Desktop.Editors.Writer.Views;
using Nexolume.Desktop.Home;
using Nexolume.Desktop.Platform;
using Nexolume.Desktop.Services;
namespace Nexolume.Desktop.Shell;

/// <summary>
/// The Nexolume shell (title bar, document tabs, status bar). Hosted by <see cref="MainWindow"/> on
/// Windows and set as the single view of the page in the browser.
/// </summary>
public partial class MainView : UserControl, IDisposable
{
    private readonly OfficeEngine engine;
    private readonly Preferences preferences = Preferences.Load();
    private readonly Dictionary<TabItem, EditorView> editors = [];
    private readonly HomeView home = new();
    private readonly TabControl tabs;
    private readonly TabItem homeTab;
    private bool disposed;

    public MainView() : this(new OfficeEngine()) { }
    public MainView(OfficeEngine engine)
    {
        this.engine = engine;
        AvaloniaXamlLoader.Load(this); SetupFileMenu(); tabs = this.FindControl<TabControl>("DocumentTabs")!;
        // Windows draws its caption buttons over the right of the title bar; a web page has none.
        if (AppPlatform.IsBrowser) this.FindControl<Border>("TitleBar")!.Padding = new Thickness(12, 3);
        homeTab = new TabItem { Header = "Accueil", Content = home }; tabs.Items.Add(homeTab); tabs.SelectedItem = homeTab;
        home.CreateRequested += kind => Run(() => CreateAsync(kind)); home.OpenRequested += () => Run(OpenPickerAsync); home.RecentRequested += path => Run(() => OpenAsync(path)); home.SetRecent(preferences.Recent);
        this.FindControl<Button>("HomeButton")!.Click += (_, _) => tabs.SelectedItem = homeTab;
        this.FindControl<Button>("OpenButton")!.Click += (_, _) => Run(OpenPickerAsync);
        this.FindControl<Button>("SaveButton")!.Click += (_, _) => Run(() => SaveCurrentAsync(false));
        this.FindControl<Button>("UndoButton")!.Click += (_, _) => Run(() => HistoryAsync("Undo")); this.FindControl<Button>("RedoButton")!.Click += (_, _) => Run(() => HistoryAsync("Redo"));
        this.FindControl<Button>("ThemeButton")!.Click += (_, _) => { preferences.Light = !preferences.Light; ApplyTheme(); preferences.Save(); }; ApplyTheme();
        this.FindControl<Button>("DismissErrorButton")!.Click += (_, _) => this.FindControl<Border>("ErrorBanner")!.IsVisible = false;
        tabs.SelectionChanged += (_, _) => UpdateChrome();
        this.FindControl<TextBox>("DocumentTitle")!.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Run(RenameAsync); e.Handled = true; } };
        // Shortcuts are caught at the top level: with nothing focused (home page, browser canvas
        // just clicked) key events are routed to the TopLevel only and never cross this view.
        AttachedToVisualTree += (_, _) => TopLevel.GetTopLevel(this)?.AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        DetachedFromVisualTree += (_, e) => (e.Root as TopLevel)?.RemoveHandler(KeyDownEvent, OnKeyDown);
        UpdateChrome();
    }

    /// <summary>Overlay used by the browser <see cref="DialogWindow"/>.</summary>
    public Panel DialogLayer => this.FindControl<Panel>("DialogHost")!;
    /// <summary>Read-only state for automated tests (browser diagnostics hook, desktop UI tests).</summary>
    public System.Text.Json.Nodes.JsonObject DescribeState() => new()
    {
        ["dialogOpen"] = DialogLayer.IsVisible,
        ["selected"] = Current?.Document.Id,
        ["focus"] = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()?.GetType().Name,
        ["documents"] = new System.Text.Json.Nodes.JsonArray(editors.Values.Select(v => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = v.Document.Id, ["kind"] = v.Document.Kind, ["title"] = v.Document.Title, ["revision"] = v.Document.Revision,
            ["dirty"] = v.Document.Dirty || v.HasDraft, ["canUndo"] = v.Document.CanUndo, ["fileName"] = v.Document.FileName,
            ["content"] = v.Document.Snapshot["content"]?.DeepClone(),
        }).ToArray()),
    };
    public bool HasUnsavedDocuments => editors.Values.Any(v => v.Document.Dirty || v.HasDraft);
    private EditorView? Current => tabs.SelectedItem is TabItem t && editors.TryGetValue(t, out var e) ? e : null;
    private void ApplyTheme() { if (Application.Current is { } app) app.RequestedThemeVariant = preferences.Light ? ThemeVariant.Light : ThemeVariant.Dark; }
    private async void Run(Func<Task> action) { try { await action(); } catch (Exception ex) { Report(ex); } }
    private void Report(Exception ex) { AppLog.Write(ex); this.FindControl<TextBlock>("StatusText")!.Text = "Une action nécessite votre attention."; this.FindControl<TextBlock>("ErrorText")!.Text = ex.Message; this.FindControl<Border>("ErrorBanner")!.IsVisible = true; }
    internal void ShowError(Exception ex) => Report(ex);
    private void SetStatus(string text) => this.FindControl<TextBlock>("StatusText")!.Text = text;

    public async Task CreateAsync(string kind) { var document = await DocumentSession.CreateAsync(engine, kind, kind switch { "writer" => "Texte sans titre", "sheets" => "Tableur sans titre", _ => "Présentation sans titre" }); AddDocument(document); }
    private void AddDocument(DocumentSession document)
    {
        EditorView view = document.Kind switch { "writer" => new WriterView(document), "sheets" => new Editors.Sheets.Views.SheetsView(document), "slides" => new Editors.Slides.Views.SlidesView(document), _ => throw new NotSupportedException("Éditeur inconnu") };
        var label = new TextBlock { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }; var close = new Button { Content = "×", Padding = new Thickness(8, 0), MinHeight = 24 }; close.Classes.Add("quiet");
        var glyph = new DesignSystem.Icons.OfficeIcon { Kind = document.Kind == "writer" ? "document" : document.Kind, Stroke = Avalonia.Media.Brush.Parse(document.Kind switch { "sheets" => "#69CCB2", "slides" => "#E9B27E", _ => "#8AB3FF" }), Width = 16, Height = 16, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Children = { glyph, label, close } };
        var tab = new TabItem { Header = header, Content = view }; editors[tab] = view; tabs.Items.Add(tab); tabs.SelectedItem = tab;
        // Typing right after New/Open goes into the document, on desktop and in the browser alike.
        void FocusOnce(object? _, Avalonia.Interactivity.RoutedEventArgs __) { view.Loaded -= FocusOnce; Avalonia.Threading.Dispatcher.UIThread.Post(view.FocusContent, Avalonia.Threading.DispatcherPriority.Input); }
        view.Loaded += FocusOnce;
        void Refresh() { label.Text = document.Title + (document.Dirty || view.HasDraft ? " •" : ""); if (Current == view) UpdateChrome(); AppPlatform.Current.SetUnsavedChanges(HasUnsavedDocuments); }
        document.Changed += (_, _) => Refresh(); view.DraftChanged += Refresh; view.Error += Report;
        close.Click += (_, _) => Run(async () => { await CloseTabAsync(tab); }); Refresh();
    }
    private void UpdateChrome()
    {
        var view = Current; var d = view?.Document;
        foreach (var item in writerMenuItems) item.IsEnabled = d?.Kind == "writer"; foreach (var item in sheetsMenuItems) item.IsEnabled = d?.Kind == "sheets"; foreach (var item in slidesMenuItems) item.IsEnabled = d?.Kind == "slides"; foreach (var item in documentMenuItems) item.IsEnabled = d is not null;
        this.FindControl<Button>("SaveButton")!.IsEnabled = d is not null; this.FindControl<Button>("UndoButton")!.IsEnabled = d?.CanUndo == true || view?.HasDraft == true; this.FindControl<Button>("RedoButton")!.IsEnabled = d?.CanRedo == true;
        var title = this.FindControl<TextBox>("DocumentTitle")!; title.IsVisible = d is not null; if (!title.IsFocused) title.Text = d?.Title ?? "";
        this.FindControl<TextBlock>("SavedLabel")!.Text = d is null ? "" : (d.Dirty || view!.HasDraft ? "À enregistrer" : AppPlatform.Current.SavedLocation);
    }
    private async Task RenameAsync() { var view = Current; if (view is null) return; var title = this.FindControl<TextBox>("DocumentTitle")!.Text; if (!string.IsNullOrWhiteSpace(title) && title != view.Document.Title) { await view.FlushAsync(); await view.Document.ExecuteAsync("RenameDocument", new { title }); } }
    private async Task HistoryAsync(string command) { var view = Current; if (view is null) return; await view.FlushAsync(); if (command == "Undo" ? view.Document.CanUndo : view.Document.CanRedo) await view.Document.ExecuteAsync(command); }
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Browser dialogs live in this view: shortcuts must not reach the documents behind them.
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || DialogLayer.IsVisible) return; var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        Func<Task>? action = e.Key switch { Key.N => () => CreateAsync("writer"), Key.O => OpenPickerAsync, Key.S => () => SaveCurrentAsync(shift), Key.Z => () => HistoryAsync(shift ? "Redo" : "Undo"), Key.Y => () => HistoryAsync("Redo"), Key.W => async () => { if (tabs.SelectedItem is TabItem t && editors.ContainsKey(t)) await CloseTabAsync(t); }, _ => null };
        if (action is not null) { e.Handled = true; Run(action); }
    }

    /// <summary>Asks about every unsaved document; false when the user cancelled one.</summary>
    public async Task<bool> CloseAllAsync()
    {
        foreach (var tab in editors.Keys.ToArray()) if (!await CloseTabAsync(tab)) return false;
        return true;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        foreach (var view in editors.Values) { view.Detach(); view.Document.Dispose(); }
        editors.Clear(); engine.Dispose();
    }
}
