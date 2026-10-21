using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Nexolume.Desktop.Editors.Writer.Views;

public partial class WriterView
{
    private const int PickerRows = 8, PickerColumns = 10;
    private readonly Border[,] pickerCells = new Border[PickerRows, PickerColumns];
    private TextBlock pickerLabel = null!;

    // Word-style size picker: hovering the grid highlights rows×columns, a click inserts that table.
    private void SetupTablePicker()
    {
        var grid = new Grid { Name = "TablePickerGrid", Margin = new Thickness(0, 6) };
        for (int r = 0; r < PickerRows; r++) grid.RowDefinitions.Add(new RowDefinition(22, GridUnitType.Pixel));
        for (int c = 0; c < PickerColumns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(22, GridUnitType.Pixel));
        pickerLabel = new TextBlock { Text = "Insérer un tableau", FontWeight = FontWeight.SemiBold };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 2, Margin = new Thickness(4), Children = { pickerLabel, grid } } };
        for (int r = 0; r < PickerRows; r++)
            for (int c = 0; c < PickerColumns; c++)
            {
                int row = r, column = c;
                var cell = new Border { Name = $"PickerCell_{r + 1}_{c + 1}", Margin = new Thickness(1), BorderThickness = new Thickness(1), BorderBrush = Brush.Parse("#8A8A8A"), Background = Brushes.White, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
                cell.PointerEntered += (_, _) => HighlightPicker(row + 1, column + 1);
                cell.PointerPressed += async (_, e) => { e.Handled = true; flyout.Hide(); await InsertTableAsync(row + 1, column + 1); };
                Grid.SetRow(cell, r); Grid.SetColumn(cell, c); grid.Children.Add(cell); pickerCells[r, c] = cell;
            }
        grid.PointerExited += (_, _) => HighlightPicker(0, 0);
        flyout.Opening += (_, _) => HighlightPicker(0, 0);
        this.FindControl<Button>("InsertTableButton")!.Flyout = flyout;
    }

    public void HighlightPicker(int rows, int columns)
    {
        for (int r = 0; r < PickerRows; r++)
            for (int c = 0; c < PickerColumns; c++)
            {
                var on = r < rows && c < columns;
                pickerCells[r, c].Background = on ? Brush.Parse("#DCE9F8") : Brushes.White;
                pickerCells[r, c].BorderBrush = Brush.Parse(on ? "#2F6FB5" : "#8A8A8A");
            }
        pickerLabel.Text = rows == 0 ? "Insérer un tableau" : $"{columns} × {rows} tableau";
    }
}
