using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using OfficeBridge.Services;
using Nexolume.Desktop.Editors;
using Nexolume.Desktop.Editors.Writer.Views;
using Nexolume.Desktop.Home;
using Nexolume.Desktop.Services;
namespace Nexolume.Desktop.Shell;

public partial class MainWindow : Window
{
    private readonly OfficeEngine engine = new();
    private readonly Preferences preferences = Preferences.Load();
    private readonly Dictionary<TabItem, EditorView> editors = [];
    private readonly HomeView home = new();
    private readonly TabControl tabs;
    private readonly TabItem homeTab;
    private bool approvedClose;
    private bool closing;
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this); SetupFileMenu(); tabs = this.FindControl<TabControl>("DocumentTabs")!;
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

        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Closing += async (_, e) => { if (approvedClose) return; e.Cancel = true; if (closing) return; closing = true; try { foreach (var tab in editors.Keys.ToArray()) if (!await CloseTabAsync(tab)) return; approvedClose = true; Close(); } catch (Exception ex) { Report(ex); } finally { closing = false; } };
        Closed += (_, _) => engine.Dispose(); UpdateChrome();
    }
    private EditorView? Current => tabs.SelectedItem is TabItem t && editors.TryGetValue(t, out var e) ? e : null;
    private void ApplyTheme() { if (Application.Current is { } app) app.RequestedThemeVariant = preferences.Light ? ThemeVariant.Light : ThemeVariant.Dark; }
    private async void Run(Func<Task> action) { try { await action(); } catch (Exception ex) { Report(ex); } }
    private void Report(Exception ex) { AppLog.Write(ex); this.FindControl<TextBlock>("StatusText")!.Text = "Une action nécessite votre attention."; this.FindControl<TextBlock>("ErrorText")!.Text = ex.Message; this.FindControl<Border>("ErrorBanner")!.IsVisible = true; }
    public async Task CreateAsync(string kind) { var document = await DocumentSession.CreateAsync(engine, kind, kind switch { "writer" => "Texte sans titre", "sheets" => "Tableur sans titre", _ => "Présentation sans titre" }); AddDocument(document); }
    private void AddDocument(DocumentSession document)
    {
        EditorView view = document.Kind switch { "writer" => new WriterView(document), "sheets" => new Editors.Sheets.Views.SheetsView(document), "slides" => new Editors.Slides.Views.SlidesView(document), _ => throw new NotSupportedException("Éditeur inconnu") };
        var label = new TextBlock { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }; var close = new Button { Content = "×", Padding = new Thickness(8, 0), MinHeight = 24 }; close.Classes.Add("quiet");
        var glyph = new DesignSystem.Icons.OfficeIcon { Kind = document.Kind == "writer" ? "document" : document.Kind, Stroke = Avalonia.Media.Brush.Parse(document.Kind switch { "sheets" => "#69CCB2", "slides" => "#E9B27E", _ => "#8AB3FF" }), Width = 16, Height = 16, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Children = { glyph, label, close } };
        var tab = new TabItem { Header = header, Content = view }; editors[tab] = view; tabs.Items.Add(tab); tabs.SelectedItem = tab;
        void Refresh() { label.Text = document.Title + (document.Dirty || view.HasDraft ? " •" : ""); if (Current == view) UpdateChrome(); }
        document.Changed += (_, _) => Refresh(); view.DraftChanged += Refresh; view.Error += Report;
        close.Click += (_, _) => Run(async () => { await CloseTabAsync(tab); }); Refresh();
    }
    private void UpdateChrome() { var view = Current; var d = view?.Document; foreach (var item in writerMenuItems) item.IsEnabled = d?.Kind == "writer"; foreach (var item in documentMenuItems) item.IsEnabled = d is not null; this.FindControl<Button>("SaveButton")!.IsEnabled = d is not null; this.FindControl<Button>("UndoButton")!.IsEnabled = d?.CanUndo == true || view?.HasDraft == true; this.FindControl<Button>("RedoButton")!.IsEnabled = d?.CanRedo == true; var title = this.FindControl<TextBox>("DocumentTitle")!; title.IsVisible = d is not null; if (!title.IsFocused) title.Text = d?.Title ?? ""; this.FindControl<TextBlock>("SavedLabel")!.Text = d is null ? "" : (d.Dirty || view!.HasDraft ? "À enregistrer" : "Enregistré sur cet ordinateur"); }
    private async Task RenameAsync() { var view = Current; if (view is null) return; var title = this.FindControl<TextBox>("DocumentTitle")!.Text; if (!string.IsNullOrWhiteSpace(title) && title != view.Document.Title) { await view.FlushAsync(); await view.Document.ExecuteAsync("RenameDocument", new { title }); } }
    private async Task HistoryAsync(string command) { var view = Current; if (view is null) return; await view.FlushAsync(); if (command == "Undo" ? view.Document.CanUndo : view.Document.CanRedo) await view.Document.ExecuteAsync(command); }
    private void OnKeyDown(object? sender, KeyEventArgs e) { if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return; var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift); Func<Task>? action = e.Key switch { Key.N => () => CreateAsync("writer"), Key.O => OpenPickerAsync, Key.S => () => SaveCurrentAsync(shift), Key.Z => () => HistoryAsync(shift ? "Redo" : "Undo"), Key.Y => () => HistoryAsync("Redo"), Key.W => async () => { if (tabs.SelectedItem is TabItem t && editors.ContainsKey(t)) await CloseTabAsync(t); }, _ => null }; if (action is not null) { e.Handled = true; Run(action); } }
}
