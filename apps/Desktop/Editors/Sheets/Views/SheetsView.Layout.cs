using Avalonia.Controls;
using System.Text.Json.Nodes;
using Nexolume.Desktop.Editors.Sheets.Controls;
namespace Nexolume.Desktop.Editors.Sheets.Views;

public partial class SheetsView
{
    private static readonly (string Label, string Op)[] RuleOps = { ("Supérieur à", "gt"), ("Inférieur à", "lt"), ("Égal à", "eq"), ("Entre", "between"), ("Contient", "contains"), ("Négatif", "neg"), ("Vide", "empty") };
    private static readonly (string Label, string Fill, string Color)[] RuleFills = { ("Rouge clair", "#F8D7DA", "#8A1C2B"), ("Vert clair", "#D4EDDA", "#1E5631"), ("Jaune", "#FFF3CD", "#7A5B00"), ("Bleu clair", "#D6E4F7", "#1F3F73"), ("Orange", "#FDE2CC", "#8A4B12") };
    private static readonly (string Label, string Kind)[] ChartKinds = { ("Histogramme", "bar"), ("Courbe", "line"), ("Secteurs", "pie") };
    private static readonly int[] ColumnSizes = { 60, 80, 100, 116, 150, 200, 300 };
    private static readonly int[] RowSizes = { 24, 32, 40, 56, 80 };
    private bool syncingLayout;

    private void SetupLayout()
    {
        var widthBox = this.FindControl<ComboBox>("ColumnWidthBox")!; var heightBox = this.FindControl<ComboBox>("RowHeightBox")!;
        widthBox.ItemsSource = ColumnSizes.Select(w => $"Largeur : {w} px").ToList(); heightBox.ItemsSource = RowSizes.Select(h => $"Hauteur : {h} px").ToList();
        widthBox.PlaceholderText = "Largeur de colonne"; heightBox.PlaceholderText = "Hauteur de ligne";
        widthBox.SelectionChanged += async (_, _) =>
        {
            if (syncingLayout || widthBox.SelectedIndex < 0) return; var size = ColumnSizes[widthBox.SelectedIndex]; syncingLayout = true; widthBox.SelectedIndex = -1; syncingLayout = false;
            var range = Surface.Selection; await EditMetaAsync(meta => { var map = meta["col_widths"]!.AsObject(); for (int c = range.Left; c <= range.Right && c < range.Left + 200; c++) map[c.ToString()] = size; });
        };
        heightBox.SelectionChanged += async (_, _) =>
        {
            if (syncingLayout || heightBox.SelectedIndex < 0) return; var size = RowSizes[heightBox.SelectedIndex]; syncingLayout = true; heightBox.SelectedIndex = -1; syncingLayout = false;
            var range = Surface.Selection; await EditMetaAsync(meta => { var map = meta["row_heights"]!.AsObject(); for (int r = range.Top; r <= range.Bottom && r < range.Top + 200; r++) map[r.ToString()] = size; });
        };
        Surface.ColumnResized += async (column, size) => await EditMetaAsync(meta => meta["col_widths"]![column.ToString()] = Math.Round(size));
        Surface.RowResized += async (row, size) => await EditMetaAsync(meta => meta["row_heights"]![row.ToString()] = Math.Round(size));

        this.FindControl<Button>("MergeButton")!.Click += async (_, _) =>
        {
            var range = Surface.Selection; if (range.Count < 2) { ShowLayoutHint("Sélectionnez au moins deux cellules à fusionner."); return; }
            if (Surface.Merges.Any(m => m.Left <= range.Right && m.Right >= range.Left && m.Top <= range.Bottom && m.Bottom >= range.Top)) { ShowLayoutHint("La sélection chevauche déjà une fusion : annulez-la d'abord."); return; }
            await EditMetaAsync(meta => meta["merges"]!.AsArray().Add(SheetGrid.RangeName(range)));
        };
        this.FindControl<Button>("UnmergeButton")!.Click += async (_, _) =>
        {
            var range = Surface.Selection; var hit = Surface.Merges.Where(m => m.Left <= range.Right && m.Right >= range.Left && m.Top <= range.Bottom && m.Bottom >= range.Top).Select(SheetGrid.RangeName).ToHashSet();
            if (hit.Count == 0) { ShowLayoutHint("Aucune cellule fusionnée dans la sélection."); return; }
            await EditMetaAsync(meta => { var merges = meta["merges"]!.AsArray(); for (int i = merges.Count - 1; i >= 0; i--) if (hit.Contains(merges[i]!.GetValue<string>())) merges.RemoveAt(i); });
        };
        this.FindControl<Button>("FreezeButton")!.Click += async (_, _) => { int rows = Surface.Row, cols = Surface.Column; await EditMetaAsync(meta => { meta["frozen_rows"] = rows; meta["frozen_cols"] = cols; }); };
        this.FindControl<Button>("UnfreezeButton")!.Click += async (_, _) => await EditMetaAsync(meta => { meta["frozen_rows"] = 0; meta["frozen_cols"] = 0; });

        var opBox = this.FindControl<ComboBox>("RuleOpBox")!; var fillBox = this.FindControl<ComboBox>("RuleFillBox")!;
        opBox.ItemsSource = RuleOps.Select(o => o.Label).ToList(); opBox.SelectedIndex = 0; fillBox.ItemsSource = RuleFills.Select(f => f.Label).ToList(); fillBox.SelectedIndex = 0;
        var value = this.FindControl<TextBox>("RuleValueBox")!; var value2 = this.FindControl<TextBox>("RuleValue2Box")!;
        opBox.SelectionChanged += (_, _) => { var op = RuleOps[Math.Max(0, opBox.SelectedIndex)].Op; value.IsEnabled = op is "gt" or "lt" or "eq" or "between" or "contains"; value2.IsEnabled = op == "between"; };
        this.FindControl<Button>("AddRuleButton")!.Click += async (_, _) =>
        {
            var op = RuleOps[Math.Max(0, opBox.SelectedIndex)].Op; var fill = RuleFills[Math.Max(0, fillBox.SelectedIndex)]; var v1 = value.Text?.Trim() ?? ""; var v2 = value2.Text?.Trim() ?? "";
            if (op is "gt" or "lt" or "eq" or "between" && !GridRule.TryParse(v1, out _)) { ShowLayoutHint("Saisissez une valeur numérique."); return; }
            if (op == "between" && !GridRule.TryParse(v2, out _)) { ShowLayoutHint("Saisissez la seconde valeur numérique."); return; }
            if (op == "contains" && v1.Length == 0) { ShowLayoutHint("Saisissez le texte à rechercher."); return; }
            var area = SheetGrid.RangeName(Surface.Selection);
            await EditMetaAsync(meta => meta["rules"]!.AsArray().Add(new JsonObject { ["range"] = area, ["op"] = op, ["value"] = v1, ["value2"] = v2, ["fill"] = fill.Fill, ["color"] = fill.Color }));
        };
        this.FindControl<Button>("ClearRulesButton")!.Click += async (_, _) => await EditMetaAsync(meta => meta["rules"] = new JsonArray());

        var kindBox = this.FindControl<ComboBox>("ChartKindBox")!; kindBox.ItemsSource = ChartKinds.Select(k => k.Label).ToList(); kindBox.SelectedIndex = 0;
        this.FindControl<Button>("InsertChartButton")!.Click += async (_, _) =>
        {
            var range = Surface.Selection; if (range.Count < 2) { ShowLayoutHint("Sélectionnez la plage de données du graphique."); return; }
            var kind = ChartKinds[Math.Max(0, kindBox.SelectedIndex)].Kind; var title = this.FindControl<TextBox>("ChartTitleBox")!.Text?.Trim() ?? ""; var area = SheetGrid.RangeName(range);
            await EditMetaAsync(meta => meta["charts"]!.AsArray().Add(new JsonObject { ["id"] = Guid.NewGuid().ToString("N")[..12], ["kind"] = kind, ["range"] = area, ["title"] = title.Length > 80 ? title[..80] : title }));
        };
        this.FindControl<Button>("DeleteChartsButton")!.Click += async (_, _) => await EditMetaAsync(meta => meta["charts"] = new JsonArray());
        opBox.SelectedIndex = -1; opBox.SelectedIndex = 0;
    }

    private void ShowLayoutHint(string text) => this.FindControl<TextBlock>("RuleSummary")!.Text = text;

    /// <summary>Current presentation metadata with every collection present, safe to mutate.</summary>
    private JsonObject ReadMeta()
    {
        var meta = Document.Snapshot["sheet_meta"]?.DeepClone() as JsonObject ?? new JsonObject();
        foreach (var key in new[] { "col_widths", "row_heights" }) if (meta[key] is not JsonObject) meta[key] = new JsonObject();
        foreach (var key in new[] { "merges", "rules", "charts" }) if (meta[key] is not JsonArray) meta[key] = new JsonArray();
        foreach (var key in new[] { "frozen_rows", "frozen_cols" }) if (meta[key] is null) meta[key] = 0;
        return meta;
    }

    private async Task EditMetaAsync(Action<JsonObject> change)
    {
        await FlushAsync();
        await Enqueue(() => { var meta = ReadMeta(); change(meta); return Document.ExecuteAsync("SetSheetMeta", new { meta }); });
    }

    private void SyncLayout()
    {
        var meta = ReadMeta();
        Surface.ColumnWidths.Clear(); Surface.RowHeights.Clear(); Surface.Merges.Clear(); Surface.Rules.Clear();
        foreach (var (key, value) in meta["col_widths"]!.AsObject()) if (int.TryParse(key, out var c) && value is not null) Surface.ColumnWidths[c] = value.GetValue<double>();
        foreach (var (key, value) in meta["row_heights"]!.AsObject()) if (int.TryParse(key, out var r) && value is not null) Surface.RowHeights[r] = value.GetValue<double>();
        Surface.FrozenRows = meta["frozen_rows"]!.GetValue<int>(); Surface.FrozenColumns = meta["frozen_cols"]!.GetValue<int>();
        foreach (var merge in meta["merges"]!.AsArray()) Surface.Merges.Add(SheetGrid.ParseRange(merge!.GetValue<string>()));
        foreach (var rule in meta["rules"]!.AsArray())
            Surface.Rules.Add(new GridRule(SheetGrid.ParseRange(rule!["range"]!.GetValue<string>()), rule["op"]!.GetValue<string>(), rule["value"]?.GetValue<string>() ?? "", rule["value2"]?.GetValue<string>() ?? "", rule["fill"]?.GetValue<string>() ?? "", rule["color"]?.GetValue<string>() ?? ""));
        var charts = meta["charts"]!.AsArray();
        var panel = this.FindControl<SheetChartPanel>("ChartPanel")!; panel.Cells = Surface.Cells; panel.Charts = charts; panel.Refresh();
        this.FindControl<Border>("ChartPane")!.IsVisible = charts.Count > 0;
        var summary = this.FindControl<TextBlock>("RuleSummary")!; var count = Surface.Rules.Count;
        summary.Text = count == 0 ? "Aucune règle" : count == 1 ? "1 règle active" : $"{count} règles actives";
    }
}
