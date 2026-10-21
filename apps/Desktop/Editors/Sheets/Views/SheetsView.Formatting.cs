using Avalonia.Controls;
using Avalonia.Media;
using System.Text.Json.Nodes;
namespace Nexolume.Desktop.Editors.Sheets.Views;

public partial class SheetsView
{
    private void SetupCellFormatting()
    {
        var formats = this.FindControl<ComboBox>("CellFormatBox")!; foreach (var (label, value) in new[] { ("Général", "general"), ("Nombre · 2 décimales", "number"), ("Monétaire · €", "currency"), ("Pourcentage", "percent"), ("Entier", "integer"), ("Millier · 1 234,00", "thousands"), ("Scientifique", "scientific") }) formats.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        formats.SelectionChanged += async (_, _) => { if (rendering || formats.SelectedItem is not ComboBoxItem item) return; await FlushAsync(); await FormatSelectionAsync("format", JsonValue.Create((string)item.Tag!)!); };
        this.FindControl<Button>("CellBoldButton")!.Click += async (_, _) => { await FlushAsync(); await FormatSelectionAsync("bold", JsonValue.Create(!CellStyle()["bold"]!.GetValue<bool>())!); };
        SetupRichStyles(); SetupFunctionCatalog(); SetupSort(); SetupLayout();
        var functions = this.FindControl<ComboBox>("AggregateBox")!; foreach (var (label, name) in new[] { ("Somme", "SUM"), ("Moyenne", "AVERAGE"), ("Minimum", "MIN"), ("Maximum", "MAX"), ("Nombre", "COUNT") }) functions.Items.Add(new ComboBoxItem { Content = label, Tag = name }); functions.SelectedIndex = 0;
        this.FindControl<Button>("InsertAggregateButton")!.Click += async (_, _) => { await FlushAsync(); if (Surface.Row == 0) return; var column = Controls.SheetGrid.CellName(Surface.Column, 0)[..^1]; var function = (string)((ComboBoxItem)functions.SelectedItem!).Tag!; await Enqueue(() => Document.ExecuteAsync("SetCell", new { address = active, input = $"={function}({column}1:{column}{Surface.Row})" })); };
    }
    private JsonNode CellStyle() => Surface.Cells[active]?["style"]?.DeepClone() ?? DefaultStyle();
    private static JsonObject DefaultStyle() => new() { ["bold"] = false, ["italic"] = false, ["underline"] = false, ["align"] = "general", ["color"] = "none", ["fill"] = "none", ["format"] = "general" };
    private bool StyleFlag(JsonNode style, string key) => style[key]?.GetValue<bool>() ?? false;
    private void UpdateCellFormatting() { var style = CellStyle(); var box = this.FindControl<ComboBox>("CellFormatBox")!; box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == style["format"]!.GetValue<string>()); this.FindControl<Button>("CellBoldButton")!.Background = style["bold"]!.GetValue<bool>() ? Brush.Parse(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light ? "#DCE8FC" : "#426497") : Brushes.Transparent; this.FindControl<Button>("InsertAggregateButton")!.IsEnabled = Surface.Row > 0; UpdateRichStyles(); }
}
