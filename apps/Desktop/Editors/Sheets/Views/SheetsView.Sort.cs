using Avalonia.Controls;
namespace Nexolume.Desktop.Editors.Sheets.Views;

public partial class SheetsView
{
    private void SetupSort()
    {
        this.FindControl<Button>("SortAscButton")!.Click += async (_, _) => await SortAsync(false);
        this.FindControl<Button>("SortDescButton")!.Click += async (_, _) => await SortAsync(true);
    }

    // A multi-cell selection is sorted as is; a single cell sorts the whole table (the bounding box of all filled cells).
    public async Task SortAsync(bool descending)
    {
        await FlushAsync();
        var range = Surface.Selection; int left = range.Left, top = range.Top, right = range.Right, bottom = range.Bottom;
        if (range.Count == 1)
        {
            left = top = int.MaxValue; right = bottom = -1;
            foreach (var address in Surface.Cells.Select(c => c.Key))
            {
                var (col, row) = Controls.SheetGrid.ParseAddress(address); left = Math.Min(left, col); top = Math.Min(top, row); right = Math.Max(right, col); bottom = Math.Max(bottom, row);
            }
            if (right < 0 || Surface.Column < left || Surface.Column > right) return;
        }
        var header = this.FindControl<CheckBox>("SortHeaderToggle")!.IsChecked == true;
        var area = $"{Controls.SheetGrid.CellName(left, top)}:{Controls.SheetGrid.CellName(right, bottom)}";
        await Enqueue(() => Document.ExecuteAsync("SortRange", new { range = area, column = Math.Clamp(Surface.Column - left, 0, right - left), descending, header }));
    }
}
