using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Text.Json.Nodes;
namespace Nexolume.Desktop.Editors.Writer.Views;

public partial class WriterView
{
    private const int MaxTableRows = 200, MaxTableColumns = 20;
    private readonly Dictionary<string, Border> tableViews = [];
    private readonly Dictionary<string, string> tableShown = [];
    private readonly Dictionary<string, JsonArray> tableDrafts = [];
    private string? activeTable;
    private int activeRow, activeColumn;
    private void SetupTables()
    {
        SetupTablePicker();
        this.FindControl<Button>("TableAddRowButton")!.Click += async (_, _) => await EditTableAsync(AddRow);
        this.FindControl<Button>("TableRemoveRowButton")!.Click += async (_, _) => await EditTableAsync(RemoveRow);
        this.FindControl<Button>("TableAddColumnButton")!.Click += async (_, _) => await EditTableAsync(AddColumn);
        this.FindControl<Button>("TableRemoveColumnButton")!.Click += async (_, _) => await EditTableAsync(RemoveColumn);
        this.FindControl<Button>("TableHeaderButton")!.Click += async (_, _) => await EditTableAsync(t => (t.Rows, !t.Header, null));
        this.FindControl<Button>("TableDeleteButton")!.Click += async (_, _) => { if (activeTable is { } id) await DeleteTableAsync(id); };
        UpdateTableButtons();
    }
    private record TableEdit(List<List<string>> Rows, bool Header);
    private static string TableKey(JsonNode rows, bool header) => rows.ToJsonString() + header;
    private JsonNode? TableParagraph(string id) => Document.Snapshot["content"]!["paragraphs"]!.AsArray().FirstOrDefault(p => p!["id"]!.GetValue<string>() == id && p["table"] is not null);
    private void UpdateTableButtons()
    {
        foreach (var name in new[] { "TableAddRowButton", "TableRemoveRowButton", "TableAddColumnButton", "TableRemoveColumnButton", "TableHeaderButton", "TableDeleteButton" })
            this.FindControl<Button>(name)!.IsEnabled = activeTable is not null;
    }
    private void RenderTable(StackPanel panel, int index, JsonNode p)
    {
        var id = p["id"]!.GetValue<string>(); var table = p["table"]!; var rows = table["rows"]!.AsArray(); var header = table["header"]!.GetValue<bool>(); var style = p["style"]!;
        if (!tableViews.TryGetValue(id, out var frame))
        {
            frame = new Border { Name = "Table_" + index, BorderBrush = Brush.Parse("#808080"), BorderThickness = new Thickness(1, 1, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.White };
            tableViews[id] = frame; panel.Children.Insert(index, frame);
        }
        var current = panel.Children.IndexOf(frame); if (current != index) panel.Children.Move(current, index);
        var key = TableKey(rows, header);
        if (!tableDrafts.ContainsKey(id) && (!tableShown.TryGetValue(id, out var shown) || shown != key))
        {
            frame.Child = BuildTable(id, index, rows, header); tableShown[id] = key;
        }
        frame.Margin = new Thickness(style["indent"]!.GetValue<int>() * 24, 4, 0, Math.Max(8, style["spacing_after"]!.GetValue<double>() * 4 / 3));
    }
    private Grid BuildTable(string id, int index, JsonArray rows, bool header)
    {
        var columns = rows[0]!.AsArray().Count; var grid = new Grid();
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < columns; c++)
            {
                var head = header && r == 0; var background = head ? Brush.Parse("#F1F3F8") : Brushes.White; var row = r; var column = c;
                var box = new TextBox { Name = $"Table_{index}_Cell_{r}_{c}", Text = rows[r]![c]!.GetValue<string>(), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Padding = new Thickness(8, 5), MinHeight = 34, FontFamily = new FontFamily("Georgia"), FontSize = 16, FontWeight = head ? FontWeight.Bold : FontWeight.Normal, Foreground = Brushes.Black, Background = background, CaretBrush = Brushes.Black };
                box.Resources["TextControlBorderBrushFocused"] = Brushes.Transparent; box.Resources["TextControlBorderBrushPointerOver"] = Brushes.Transparent;
                foreach (var name in new[] { "TextControlBackground", "TextControlBackgroundFocused", "TextControlBackgroundPointerOver" }) box.Resources[name] = background;
                foreach (var name in new[] { "TextControlForeground", "TextControlForegroundFocused", "TextControlForegroundPointerOver" }) box.Resources[name] = Brushes.Black;
                box.GotFocus += (_, _) => { if (activeImage is not null) SelectImage(null); activeTable = id; activeRow = row; activeColumn = column; UpdateTableButtons(); this.FindControl<Button>("DeleteParagraphButton")!.IsEnabled = true; };
                box.KeyDown += async (_, e) =>
                {
                    if (e.Key != Avalonia.Input.Key.Tab) return; e.Handled = true; var back = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift);
                    var position = row * columns + column + (back ? -1 : 1);
                    if (position >= 0 && position < rows.Count * columns) FocusCell(id, position / columns, position % columns);
                    else if (!back) { activeRow = row; activeColumn = 0; await EditTableAsync(AddRow); }
                };
                box.TextChanged += (_, _) => { if (!rendering) CaptureTable(id, grid); };
                var cell = new Border { BorderBrush = Brush.Parse("#808080"), BorderThickness = new Thickness(0, 0, 1, 1), Child = box };
                Grid.SetRow(cell, r); Grid.SetColumn(cell, c); grid.Children.Add(cell);
            }
        }
        return grid;
    }
    private static JsonArray ReadGrid(Grid grid)
    {
        var rows = new JsonArray(); var columns = grid.ColumnDefinitions.Count;
        for (var r = 0; r < grid.RowDefinitions.Count; r++)
        {
            var row = new JsonArray();
            for (var c = 0; c < columns; c++) row.Add(((TextBox)((Border)grid.Children[r * columns + c]).Child!).Text ?? "");
            rows.Add(row);
        }
        return rows;
    }
    private void CaptureTable(string id, Grid grid)
    {
        if (TableParagraph(id) is not { } model) return;
        var rows = ReadGrid(grid);
        if (rows.ToJsonString() == model["table"]!["rows"]!.ToJsonString()) { if (tableDrafts.Remove(id)) DraftUpdated(); return; }
        if (DateTime.UtcNow - lastTyping > TimeSpan.FromSeconds(1.5)) group = Guid.NewGuid().ToString();
        lastTyping = DateTime.UtcNow; tableDrafts[id] = rows; timer.Stop(); timer.Start(); DraftUpdated();
    }
    private async Task FlushTablesAsync()
    {
        var pending = tableDrafts.ToArray(); tableDrafts.Clear();
        foreach (var (id, rows) in pending)
        {
            if (TableParagraph(id) is not { } model) continue;
            var header = model["table"]!["header"]!.GetValue<bool>(); var table = new JsonObject { ["rows"] = rows.DeepClone(), ["header"] = header };
            tableShown[id] = TableKey(rows, header);
            await Enqueue(() => Document.ExecuteAsync("SetParagraph", new { id, table, group }));
            if (LastError is not null) { tableShown.Remove(id); if (!tableDrafts.ContainsKey(id)) tableDrafts[id] = rows; }
        }
        await Pending; DraftUpdated();
    }
    private void RemoveTableView(StackPanel panel, string id)
    {
        if (tableViews.Remove(id, out var frame)) panel.Children.Remove(frame);
        tableShown.Remove(id); tableDrafts.Remove(id);
        if (activeTable == id) { activeTable = null; UpdateTableButtons(); }
    }
    private void FocusCell(string id, int row, int column)
    {
        if (!tableViews.TryGetValue(id, out var frame) || frame.Child is not Grid grid) return;
        var columns = grid.ColumnDefinitions.Count; row = Math.Clamp(row, 0, grid.RowDefinitions.Count - 1); column = Math.Clamp(column, 0, columns - 1);
        if (((Border)grid.Children[row * columns + column]).Child is TextBox box) box.Focus();
    }
    private async Task DeleteTableAsync(string id)
    {
        await FlushAsync(); activeTable = null; UpdateTableButtons();
        await Enqueue(() => Document.ExecuteAsync("DeleteParagraph", new { id }));
    }
    private async Task EditTableAsync(Func<TableEdit, (List<List<string>> Rows, bool Header, string? Error)> change)
    {
        if (activeTable is not { } id) return; await FlushAsync();
        if (TableParagraph(id) is not { } p) return;
        var rows = p["table"]!["rows"]!.AsArray().Select(r => r!.AsArray().Select(c => c!.GetValue<string>()).ToList()).ToList();
        var (next, header, error) = change(new TableEdit(rows, p["table"]!["header"]!.GetValue<bool>()));
        if (error is not null) { SetStatus(error); return; }
        var table = new { rows = next, header }; group = Guid.NewGuid().ToString();
        await Enqueue(() => Document.ExecuteAsync("SetParagraph", new { id, table, group }));
        FocusCell(id, activeRow, activeColumn);
    }
    private (List<List<string>>, bool, string?) AddRow(TableEdit t)
    {
        if (t.Rows.Count >= MaxTableRows) return (t.Rows, t.Header, $"Un tableau ne peut pas dépasser {MaxTableRows} lignes.");
        var at = Math.Min(activeRow, t.Rows.Count - 1) + 1; t.Rows.Insert(at, t.Rows[0].Select(_ => "").ToList()); activeRow = at; return (t.Rows, t.Header, null);
    }
    private (List<List<string>>, bool, string?) RemoveRow(TableEdit t)
    {
        if (t.Rows.Count <= 1) return (t.Rows, t.Header, "Un tableau garde au moins une ligne : utilisez « Supprimer le tableau ».");
        t.Rows.RemoveAt(Math.Min(activeRow, t.Rows.Count - 1)); activeRow = Math.Min(activeRow, t.Rows.Count - 1); return (t.Rows, t.Header, null);
    }
    private (List<List<string>>, bool, string?) AddColumn(TableEdit t)
    {
        if (t.Rows[0].Count >= MaxTableColumns) return (t.Rows, t.Header, $"Un tableau ne peut pas dépasser {MaxTableColumns} colonnes.");
        var at = Math.Min(activeColumn, t.Rows[0].Count - 1) + 1; foreach (var row in t.Rows) row.Insert(at, ""); activeColumn = at; return (t.Rows, t.Header, null);
    }
    private (List<List<string>>, bool, string?) RemoveColumn(TableEdit t)
    {
        if (t.Rows[0].Count <= 1) return (t.Rows, t.Header, "Un tableau garde au moins une colonne : utilisez « Supprimer le tableau ».");
        var at = Math.Min(activeColumn, t.Rows[0].Count - 1); foreach (var row in t.Rows) row.RemoveAt(at); activeColumn = Math.Min(activeColumn, t.Rows[0].Count - 1); return (t.Rows, t.Header, null);
    }
    public async Task InsertTableAsync(int rowCount, int columnCount)
    {
        rowCount = Math.Clamp(rowCount, 1, MaxTableRows); columnCount = Math.Clamp(columnCount, 1, MaxTableColumns);
        await FlushAsync();
        var list = Document.Snapshot["content"]!["paragraphs"]!.AsArray(); var anchor = activeTable ?? activeImage ?? active;
        var at = list.Select((p, i) => (p, i)).FirstOrDefault(x => x.p!["id"]!.GetValue<string>() == anchor).i + 1;
        var leading = list[at - 1]!["table"] is not null; var trailing = at >= list.Count || list[at]!["table"] is not null || list[at]!["image"] is not null;
        var id = Guid.NewGuid().ToString(); var commands = new List<object>(); var index = at;
        if (leading) commands.Add(new { command = "InsertParagraph", @params = new { id = Guid.NewGuid().ToString(), index = index++, text = "" } });
        var table = new { rows = Enumerable.Range(0, rowCount).Select(_ => Enumerable.Repeat("", columnCount).ToArray()).ToArray(), header = true };
        commands.Add(new { command = "InsertParagraph", @params = new { id, index = index++, text = "", table } });
        if (trailing) commands.Add(new { command = "InsertParagraph", @params = new { id = Guid.NewGuid().ToString(), index, text = "" } });
        group = Guid.NewGuid().ToString();
        await Enqueue(() => Document.ExecuteAsync("Batch", new { commands = commands.ToArray() }));
        if (LastError is not null) { SetStatus("Impossible d’insérer le tableau : " + LastError); return; }
        activeRow = activeColumn = 0; FocusCell(id, 0, 0);
    }
}
