using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using OfficeBridge.Services;
using Nexolume.Desktop.Editors.Sheets.Controls;
namespace Nexolume.Desktop.Editors.Sheets.Views;

public partial class SheetsView : EditorView
{
    public SheetGrid Surface { get; }
    private readonly TextBox input;
    private string active = "A1";
    private readonly TextBox editor;
    private bool draft, rendering, editing;
    public override bool HasDraft => draft;
    public SheetsView(DocumentSession document) : base(document)
    {
        AvaloniaXamlLoader.Load(this); Surface = this.FindControl<SheetGrid>("GridSurface")!; input = this.FindControl<TextBox>("CellInput")!;
        Surface.SelectionChanged += async () => { await FlushAsync(); active = Surface.Address; LoadInput(); UpdateSelectionStatus(); };
        editor = this.FindControl<TextBox>("CellEditor")!;
        Surface.EditRequested += BeginEdit;
        Surface.ViewportChanged += () => { if (editing) PlaceEditor(); };
        editor.TextChanged += (_, _) => { if (editing) input.Text = editor.Text ?? ""; };
        editor.LostFocus += async (_, _) => await EndEditAsync(true);
        editor.KeyDown += async (_, e) =>
        {
            int dx = 0, dy = 0, shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
            switch (e.Key)
            {
                case Key.Enter: dy = shift; break;
                case Key.Tab: dx = shift; break;
                case Key.Down: dy = 1; break;
                case Key.Up: dy = -1; break;
                case Key.Escape: e.Handled = true; await EndEditAsync(false); Surface.Focus(); return;
                default: return;
            }
            e.Handled = true; await EndEditAsync(true); Surface.Focus(); Surface.Select(Surface.Column + dx, Surface.Row + dy);
        };
        Surface.ClearRequested += async () => await ClearSelectionAsync();
        input.TextChanged += (_, _) => { if (!rendering) { draft = (input.Text ?? "") != (Document.Snapshot["content"]!["cells"]?[active]?["input"]?.GetValue<string>() ?? ""); DraftUpdated(); } };
        input.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await FlushAsync(); Surface.Focus(); Surface.Select(Surface.Column, Surface.Row + 1); } else if (e.Key == Key.Escape) { draft = false; LoadInput(); Surface.Focus(); DraftUpdated(); } };
        input.LostFocus += async (_, _) => await FlushAsync();
        this.FindControl<Button>("ApplyCell")!.Click += async (_, _) => { await FlushAsync(); Surface.Focus(); };
        SetupTools(); document.Changed += OnChanged; Render(); LoadInput();
    }
    private void PlaceEditor()
    {
        if (Surface.ActiveCellRect() is not { } rect) { _ = EndEditAsync(true); return; }
        editor.Margin = new Avalonia.Thickness(rect.X, rect.Y, 0, 0); editor.Height = rect.Height; editor.MinWidth = rect.Width; editor.FontSize = 13 * Surface.Zoom;
    }
    private void BeginEdit(string? text)
    {
        if (Surface.ActiveCellRect() is null) return;
        var value = text ?? input.Text ?? ""; editing = true; PlaceEditor();
        editor.Text = value; editor.IsVisible = true; editor.Focus();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { editor.SelectionStart = editor.SelectionEnd = value.Length; editor.CaretIndex = value.Length; }, Avalonia.Threading.DispatcherPriority.Input);
    }
    private async Task EndEditAsync(bool commit)
    {
        if (!editing) return; editing = false; editor.IsVisible = false;
        if (commit) await FlushAsync(); else { draft = false; LoadInput(); DraftUpdated(); }
    }
    private void OnChanged(object? sender, EventArgs e) { Render(); if (!draft) LoadInput(); }
    private void Render() { Surface.Cells = Document.Snapshot["content"]!["cells"]!.AsObject(); SyncLayout(); Surface.InvalidateVisual(); UpdateSelectionStatus(); }
    private void LoadInput() { rendering = true; input.Text = Document.Snapshot["content"]!["cells"]?[active]?["input"]?.GetValue<string>() ?? ""; this.FindControl<TextBox>("CellAddress")!.Text = Surface.SelectionAddress; this.FindControl<Button>("SumCellButton")!.IsEnabled = Surface.Row > 0; UpdateCellFormatting(); rendering = false; }
    public override async Task FlushAsync() { if (draft) { draft = false; var address = active; var text = input.Text ?? ""; await Enqueue(() => Document.ExecuteAsync("SetCell", new { address, input = text })); if (LastError is not null) draft = true; DraftUpdated(); } await Pending; }
    public override void Detach() => Document.Changed -= OnChanged;
}
