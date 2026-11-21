using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using System.Text;
using System.Text.Json.Nodes;

namespace Nexolume.Desktop.Editors.Sheets.Views;

public partial class SheetsView
{
    private void SetupDataTools()
    {
        foreach (var (name, mode) in new[] { ("FillDownButton", "down"), ("FillRightButton", "right"), ("SeriesDownButton", "series_down"), ("SeriesRightButton", "series_right") })
            this.FindControl<Button>(name)!.Click += async (_, _) => await FillSelectionAsync(mode);
        this.FindControl<Button>("ImportCsvButton")!.Click += async (_, _) =>
        {
            await FlushAsync();
            await Enqueue(async () =>
            {
                if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
                var files = await storage.OpenFilePickerAsync(new() { Title = "Importer à partir de la cellule sélectionnée", AllowMultiple = false, FileTypeFilter = [new("Tableau CSV ou TSV") { Patterns = ["*.csv", "*.tsv", "*.txt"] }] });
                if (files.FirstOrDefault() is not { } file) return;
                if (HasDraft) throw new InvalidOperationException("Validez la cellule active avant d’importer.");
                var bytes = await Platform.AppPlatform.ReadAllBytesAsync(file, CsvLimit, CsvTooLarge);
                await ImportCsvTextCoreAsync(DecodeCsv(bytes));
            });
        };
        this.FindControl<Button>("ExportCsvButton")!.Click += async (_, _) =>
        {
            await FlushAsync();
            await Enqueue(async () =>
            {
                var text = CsvText();
                await Platform.AppPlatform.Current.SaveFileAsync(this, new() { Title = "Exporter les résultats de la sélection en CSV", SuggestedFileName = Document.Title + ".csv", DefaultExtension = "csv", ShowOverwritePrompt = true, FileTypeChoices = [new("CSV UTF-8 · point-virgule") { Patterns = ["*.csv"] }] }, "text/csv", stream => WriteCsvAsync(stream, text));
            });
        };
        this.FindControl<Button>("TransposeButton")!.Click += async (_, _) =>
        {
            await FlushAsync();
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && await clipboard.TryGetTextAsync() is { } text) await PasteTransposedAsync(text);
        };
        Surface.KeyDown += async (_, e) =>
        {
            if (e.KeyModifiers != KeyModifiers.Control || e.Key is not (Key.D or Key.R)) return;
            e.Handled = true; await FillSelectionAsync(e.Key == Key.D ? "down" : "right");
        };
    }

    public async Task FillSelectionAsync(string direction)
    {
        await FlushAsync();
        await Enqueue(async () =>
        {
            var range = Surface.Selection;
            foreach (var merged in Surface.Merges)
            {
                if (merged.Left <= range.Right && merged.Right >= range.Left && merged.Top <= range.Bottom && merged.Bottom >= range.Top)
                    throw new InvalidOperationException("Séparez les cellules fusionnées avant la recopie.");
            }
            await Document.ExecuteAsync("FillRange", new { range = $"{Controls.SheetGrid.CellName(range.Left, range.Top)}:{Controls.SheetGrid.CellName(range.Right, range.Bottom)}", direction });
        });
    }

    public async Task ImportCsvAsync(string path) { await FlushAsync(); await Enqueue(() => ImportCsvCoreAsync(path)); }
    private const int CsvLimit = 4_000_000;
    private const string CsvTooLarge = "Le fichier CSV dépasse 4 Mo.";
    private static string DecodeCsv(byte[] bytes)
    {
        try { var text = new UTF8Encoding(false, true).GetString(bytes); return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text; }
        catch (DecoderFallbackException) { throw new InvalidOperationException("Le fichier CSV doit être encodé en UTF-8."); }
    }
    private async Task ImportCsvCoreAsync(string path)
    {
        // Finish any pending formula-bar input before opening the file.
        if (HasDraft) throw new InvalidOperationException("Validez la cellule active avant d’importer.");
        if (new FileInfo(path).Length > CsvLimit) throw new InvalidOperationException(CsvTooLarge);
        await ImportCsvTextCoreAsync(await File.ReadAllTextAsync(path, new UTF8Encoding(false, true)));
    }
    /// <summary>Imports CSV text at the selected cell (used by tests and the browser picker).</summary>
    public async Task ImportCsvTextAsync(string text) { await FlushAsync(); await Enqueue(() => ImportCsvTextCoreAsync(text)); }
    private async Task ImportCsvTextCoreAsync(string text)
    {
        if (HasDraft) throw new InvalidOperationException("Validez la cellule active avant d’importer.");
        var choice = this.FindControl<ComboBox>("CsvSeparatorBox")!.SelectedIndex;
        var separator = choice switch { 1 => ';', 2 => ',', 3 => '\t', _ => TabularData.DetectSeparator(text) };
        await ApplyTabularAsync(TabularData.Parse(text, separator));
    }

    public async Task ExportCsvAsync(string path) { await FlushAsync(); await Enqueue(() => ExportCsvCoreAsync(path)); }
    private Task ExportCsvCoreAsync(string path) { var text = CsvText(); return Platform.AppPlatform.WriteAtomicAsync(path, stream => WriteCsvAsync(stream, text)); }
    private static async Task WriteCsvAsync(Stream stream, string text)
    {
        var encoding = new UTF8Encoding(true);
        await stream.WriteAsync(encoding.GetPreamble()); await stream.WriteAsync(encoding.GetBytes(text));
    }
    private string CsvText()
    {
        var range = Surface.Selection; _ = SelectionCells().ToArray();
        var rows = Enumerable.Range(range.Top, range.Bottom - range.Top + 1).Select(r => Enumerable.Range(range.Left, range.Right - range.Left + 1).Select(c => Surface.Cells[Controls.SheetGrid.CellName(c, r)]?["display"]?.GetValue<string>() ?? ""));
        var separator = this.FindControl<ComboBox>("CsvSeparatorBox")!.SelectedIndex switch { 2 => ',', 3 => '\t', _ => ';' };
        return TabularData.Write(rows, separator);
    }

    public async Task PasteTransposedAsync(string text)
    {
        await FlushAsync();
        await Enqueue(async () =>
        {
            var rows = TabularData.Parse(text, '\t'); var width = rows.Max(r => r.Count);
            var transposed = Enumerable.Range(0, width).Select(c => rows.Select(row => c < row.Count ? row[c] : "").ToList()).ToList();
            await ApplyTabularAsync(transposed);
        });
    }

    private async Task ApplyTabularAsync(List<List<string>> rows)
    {
        var range = Surface.Selection; var width = rows.Max(r => r.Count);
        if (rows.Count * (long)width > 1000 || range.Left + width > 16384 || range.Top + rows.Count > 1048576)
            throw new InvalidOperationException("Le tableau dépasse la feuille ou la limite de 1 000 cellules.");
        var commands = new List<object>();
        for (int r = 0; r < rows.Count; r++) for (int c = 0; c < width; c++)
            commands.Add(new { command = "SetCell", @params = new { address = Controls.SheetGrid.CellName(range.Left + c, range.Top + r), input = c < rows[r].Count ? rows[r][c] : "" } });
        await Document.ExecuteAsync("Batch", new { commands });
        Surface.SelectRange(range.Left, range.Top, range.Left + width - 1, range.Top + rows.Count - 1);
    }
}
