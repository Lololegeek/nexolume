using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
namespace Nexolume.Desktop.Editors.Sheets.Views;

public partial class SheetsView
{
    private void SetupTools()
    {
        SetupCellFormatting(); SetupRangeTools();
        var vertical = this.FindControl<ScrollBar>("VerticalScroll")!; var horizontal = this.FindControl<ScrollBar>("HorizontalScroll")!;
        Surface.ViewportChanged += () => { vertical.Value = Surface.FirstRow; horizontal.Value = Surface.FirstColumn; };
        vertical.ValueChanged += (_, _) => { Surface.FirstRow = (int)vertical.Value; Surface.InvalidateVisual(); }; horizontal.ValueChanged += (_, _) => { Surface.FirstColumn = (int)horizontal.Value; Surface.InvalidateVisual(); };
        this.FindControl<Button>("CopyCellButton")!.Click += async (_, _) => await CopySelectedAsync();
        this.FindControl<Button>("ClearCellButton")!.Click += async (_, _) => await ClearSelectionAsync();
        this.FindControl<Button>("SumCellButton")!.Click += async (_, _) => { await FlushAsync(); var column = Controls.SheetGrid.CellName(Surface.Column, 0)[..^1]; await Enqueue(() => Document.ExecuteAsync("SetCell", new { address = active, input = $"=SUM({column}1:{column}{Surface.Row})" })); };
        this.FindControl<TextBox>("CellAddress")!.KeyDown += async (_, e) => { if (e.Key != Key.Enter) return; e.Handled = true; await NavigateToAsync(this.FindControl<TextBox>("CellAddress")!.Text ?? ""); };
    }
}
