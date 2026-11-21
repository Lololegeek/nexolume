using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
namespace Nexolume.Desktop.Editors.Sheets.Views;

public partial class SheetsView
{
    private void SetupRangeTools()
    {
        foreach (var (name, key) in new[] { ("SheetHomeTab", "Home"), ("SheetFormulasTab", "Formulas"), ("SheetLayoutTab", "Layout"), ("SheetInsertTab", "Insert"), ("SheetDataTab", "Data"), ("SheetViewTab", "View") })
            this.FindControl<Button>(name)!.Click += (_, _) => ShowSheetRibbon(key);
        this.FindControl<Button>("MoreFunctionsButton")!.Click += (_, _) => ShowSheetRibbon("Formulas");
        this.FindControl<Button>("PasteCellsButton")!.Click += async (_, _) => await PasteClipboardAsync();
        this.FindControl<CheckBox>("GridLinesToggle")!.IsCheckedChanged += (_, _) => { Surface.ShowGridLines = this.FindControl<CheckBox>("GridLinesToggle")!.IsChecked == true; Surface.InvalidateVisual(); };
        this.FindControl<CheckBox>("ShowFormulasToggle")!.IsCheckedChanged += (_, _) => { Surface.ShowFormulas = this.FindControl<CheckBox>("ShowFormulasToggle")!.IsChecked == true; Surface.InvalidateVisual(); };
        this.FindControl<Slider>("SheetZoomSlider")!.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) { Surface.Zoom = this.FindControl<Slider>("SheetZoomSlider")!.Value / 100; Surface.InvalidateVisual(); this.FindControl<TextBlock>("SheetZoomLabel")!.Text = $"{Surface.Zoom * 100:0} %"; } };
        Surface.KeyDown += async (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            if (e.Key == Key.C) { e.Handled = true; await CopySelectedAsync(); }
            else if (e.Key == Key.V) { e.Handled = true; await PasteClipboardAsync(); }
        };
        var menu = new ContextMenu();
        foreach (var (label, action) in new (string, Func<Task>)[] { ("Copier", () => CopySelectedAsync()), ("Copier les résultats", () => CopySelectedAsync(true)), ("Coller", PasteClipboardAsync), ("Effacer la sélection", ClearSelectionAsync) })
        { var item = new MenuItem { Header = label }; item.Click += async (_, _) => await action(); menu.Items.Add(item); }
        Surface.ContextMenu = menu;
    }
    private void ShowSheetRibbon(string key)
    {
        foreach (var name in new[] { "Home", "Formulas", "Layout", "Insert", "Data", "View" })
        {
            this.FindControl<Grid>("Sheet" + name + "Ribbon")!.IsVisible = name == key;
            this.FindControl<Button>("Sheet" + name + "Tab")!.Classes.Set("selected", name == key);
        }
    }
    private IEnumerable<string> SelectionCells()
    {
        var range = Surface.Selection;
        if (range.Count > 1000) throw new InvalidOperationException("Sélectionnez au maximum 1 000 cellules pour cette opération.");
        for (int r = range.Top; r <= range.Bottom; r++) for (int c = range.Left; c <= range.Right; c++) yield return Controls.SheetGrid.CellName(c, r);
    }
    public async Task NavigateToAsync(string address)
    {
        await FlushAsync();
        await Enqueue(() =>
        {
            static (int Col, int Row) Parse(string value)
            {
                var match = System.Text.RegularExpressions.Regex.Match(value, "^([A-Z]{1,3})([1-9][0-9]{0,6})$");
                if (!match.Success) throw new InvalidOperationException("Utilisez une adresse comme B12 ou une plage comme A1:C6.");
                var col = 0; foreach (var c in match.Groups[1].Value) col = col * 26 + c - 'A' + 1;
                var row = int.Parse(match.Groups[2].Value);
                if (col > 16384 || row > 1048576) throw new InvalidOperationException("Adresse hors de la feuille.");
                return (col - 1, row - 1);
            }
            var parts = address.Trim().ToUpperInvariant().Split(':');
            if (parts.Length > 2) throw new InvalidOperationException("La plage doit contenir deux adresses, comme A1:C6.");
            var start = Parse(parts[0]); var end = parts.Length == 2 ? Parse(parts[1]) : start;
            Surface.SelectRange(start.Col, start.Row, end.Col, end.Row); Surface.Focus(); return Task.CompletedTask;
        });
    }
    public string SelectionText(bool valuesOnly = false)
    {
        var range = Surface.Selection; _ = SelectionCells().ToArray();
        static string Escape(string s) => s.IndexOfAny(['\t', '\n', '\r', '"']) >= 0 ? '"' + s.Replace("\"", "\"\"") + '"' : s;
        return string.Join("\r\n", Enumerable.Range(range.Top, range.Bottom - range.Top + 1).Select(r => string.Join('\t', Enumerable.Range(range.Left, range.Right - range.Left + 1).Select(c => Escape(Surface.Cells[Controls.SheetGrid.CellName(c, r)]?[valuesOnly ? "display" : "input"]?.GetValue<string>() ?? "")))));
    }
    public async Task CopySelectedAsync(bool valuesOnly = false)
    {
        await FlushAsync();
        await Enqueue(async () => { var text = SelectionText(valuesOnly); if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text); });
    }
    public async Task PasteClipboardAsync()
    {
        await FlushAsync();
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && await clipboard.TryGetTextAsync() is { } text) await PasteTextAsync(text);
    }
    public async Task PasteTextAsync(string text)
    {
        await FlushAsync();
        await Enqueue(async () =>
        {
            await ApplyTabularAsync(TabularData.Parse(text, '\t'));
        });
    }
    public async Task ClearSelectionAsync()
    {
        await FlushAsync();
        await Enqueue(async () => { var commands = SelectionCells().Where(a => Surface.Cells[a] is not null).Select(a => new { command = "SetCell", @params = new { address = a, input = "" } }).ToArray(); if (commands.Length > 0) await Document.ExecuteAsync("Batch", new { commands }); });
    }
    private async Task FormatSelectionAsync(string property, JsonNode value)
    {
        await Enqueue(async () =>
        {
            var commands = SelectionCells().Select(address => { var style = Surface.Cells[address]?["style"]?.DeepClone() ?? DefaultStyle(); style[property] = value.DeepClone(); return new { command = "SetCellStyle", @params = new { address, style } }; }).ToArray();
            await Document.ExecuteAsync("Batch", new { commands });
        });
    }
    private void UpdateSelectionStatus()
    {
        var range = Surface.Selection;
        this.FindControl<TextBlock>("SelectionHint")!.Text = range.Count == 1 ? $"{Surface.Address} · Entrée pour modifier" : $"{Surface.SelectionAddress} · {range.Count:N0} cellules";
        var values = new List<double>();
        if (range.Count <= 1000) foreach (var address in SelectionCells())
        {
            var cell = Surface.Cells[address]; if (cell is null) continue;
            if (double.TryParse(cell["input"]?.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) values.Add(value);
            else if (cell["input"]?.GetValue<string>().StartsWith('=') == true && double.TryParse(cell["display"]?.GetValue<string>(), NumberStyles.Currency, CultureInfo.GetCultureInfo("fr-FR"), out value)) values.Add(value);
        }
        this.FindControl<TextBlock>("CellSummary")!.Text = values.Count > 0 && range.Count > 1 ? $"Moyenne {values.Average():G6}   ·   Somme {values.Sum():G6}" : $"{Surface.Cells.Count:N0} cellules renseignées";
    }
}
