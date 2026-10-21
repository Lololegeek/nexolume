using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using OfficeBridge.Services;
using Nexolume.Desktop.Editors.Sheets.Views;
internal static class SheetsChecks
{
    public static void Run()
    {
        using var engine = new OfficeEngine(); using var doc = Program.Get(DocumentSession.CreateAsync(engine, "sheets", "Fonctions et styles")); var view = new SheetsView(doc); var window = new Window { Content = view, Width = 1440, Height = 950 }; window.Show(); Dispatcher.UIThread.RunJobs();
        string Cell(string a, string key = "display") => doc.Snapshot["content"]!["cells"]![a]![key]!.GetValue<string>();
        foreach (var (a, v) in new[] { ("A1", "4"), ("A2", "6"), ("A3", "10"), ("B1", "pomme"), ("B2", "poire") }) Program.Pump(doc.ExecuteAsync("SetCell", new { address = a, input = v }));
        Program.Pump(doc.ExecuteAsync("SetCell", new { address = "C1", input = "=SI(SOMME($A$1:A3)>15;\"grand\";\"petit\")" })); Program.Check(Cell("C1") == "grand", "IF/SUM with absolute range and French aliases");
        Program.Pump(doc.ExecuteAsync("SetCell", new { address = "C2", input = "=B1&\" \"&MAJUSCULE(B2)" })); Program.Check(Cell("C2") == "pomme POIRE", "Text operators and functions");
        view.Surface.SelectRange(0, 0, 0, 2); Dispatcher.UIThread.RunJobs();
        view.FindControl<ComboBox>("FunctionCatalogBox")!.SelectedIndex = 1; Program.Click(view, "InsertFunctionButton"); Program.Pump(view.Pending); OfficeFeatureChecks.Until(() => doc.Snapshot["content"]!["cells"]!["A4"] is not null);
        Program.Check(Cell("A4", "input") == "=AVERAGE(A1:A3)" && Cell("A4") == "6,67" || Cell("A4") == "6.666666666666667" || Cell("A4").StartsWith("6,6") || Cell("A4").StartsWith("6.6"), "Function catalog inserts under the selection");
        view.Surface.Select(1, 0); Dispatcher.UIThread.RunJobs();
        Program.Click(view, "CellItalicButton"); Program.Pump(view.Pending); Program.Click(view, "CellUnderlineButton"); Program.Pump(view.Pending); Program.Click(view, "CellAlignCenterButton"); Program.Pump(view.Pending);
        view.FindControl<ComboBox>("CellColorBox")!.SelectedIndex = 2; Program.Pump(view.Pending); view.FindControl<ComboBox>("CellFillBox")!.SelectedIndex = 1; Program.Pump(view.Pending);
        var style = doc.Snapshot["content"]!["cells"]!["B1"]!["style"]!;
        Program.Check(style["italic"]!.GetValue<bool>() && style["underline"]!.GetValue<bool>() && style["align"]!.GetValue<string>() == "center" && style["color"]!.GetValue<string>() == "#C00000" && style["fill"]!.GetValue<string>() == "#FFF2A8", "Italic, underline, alignment, text and fill colours");
        view.Surface.Select(0, 0); Dispatcher.UIThread.RunJobs(); view.FindControl<ComboBox>("CellFormatBox")!.SelectedIndex = 6; Program.Pump(view.Pending); Program.Check(Cell("A1").Contains('E'), "Scientific format");
        var path = Path.Combine(Program.Artifacts, "sheets-styles.ossheet"); Program.Pump(doc.SaveAsync(path)); using var reopened = Program.Get(DocumentSession.OpenAsync(engine, path)); Program.Check(System.Text.Json.Nodes.JsonNode.DeepEquals(doc.Snapshot["content"], reopened.Snapshot["content"]), "Rich styles roundtrip");
        view.Surface.Select(3, 4); view.Surface.Focus(); Dispatcher.UIThread.RunJobs();
        view.Surface.RaiseEvent(new Avalonia.Input.TextInputEventArgs { RoutedEvent = Avalonia.Input.InputElement.TextInputEvent, Text = "a" }); Dispatcher.UIThread.RunJobs();
        var editor = view.FindControl<TextBox>("CellEditor")!; Program.Check(editor.IsVisible && editor.Text == "a", "Typing on a selected cell opens the in-cell editor with the first character");
        editor.Text = "abc"; Dispatcher.UIThread.RunJobs();
        editor.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter }); Dispatcher.UIThread.RunJobs(); Program.Pump(view.Pending);
        Program.Check(Cell("D5", "input") == "abc" && !editor.IsVisible && view.Surface.Row == 5 && view.Surface.Column == 3, "Enter commits the typed cell and moves down");
        view.Surface.Select(3, 4); view.Surface.RaiseEvent(new Avalonia.Input.TextInputEventArgs { RoutedEvent = Avalonia.Input.InputElement.TextInputEvent, Text = "z" }); Dispatcher.UIThread.RunJobs();
        editor.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape }); Dispatcher.UIThread.RunJobs(); Program.Pump(view.Pending);
        Program.Check(Cell("D5", "input") == "abc" && !editor.IsVisible, "Escape cancels the in-cell edit");
        foreach (var (a, v) in new[] { ("F1", "Nom"), ("G1", "Age"), ("F2", "zoé"), ("G2", "30"), ("F3", "alex"), ("G3", "9"), ("F4", "marc"), ("G4", "100") }) Program.Pump(doc.ExecuteAsync("SetCell", new { address = a, input = v }));
        view.Surface.SelectRange(5, 0, 6, 3); Dispatcher.UIThread.RunJobs(); Program.Click(view, "SortAscButton"); Program.Pump(view.Pending);
        Program.Check(Cell("F2") == "zoé" || Cell("F2") == "alex", "Sort button runs");
        view.Surface.Select(6, 1); Dispatcher.UIThread.RunJobs(); Program.Click(view, "SortAscButton"); Program.Pump(view.Pending);
        Program.Check(Cell("F2") == "alex" && Cell("G2") == "9" && Cell("F4") == "marc" && Cell("F1") == "Nom", "Sort by the active column keeps rows together and the header in place");
        Program.Click(view, "SortDescButton"); Program.Pump(view.Pending);
        Program.Check(Cell("F2") == "marc" && Cell("G2") == "100", "Descending sort");
        JsonNode? Meta() => doc.Snapshot["sheet_meta"];
        view.Surface.SelectRange(0, 0, 1, 1); Dispatcher.UIThread.RunJobs(); Program.Click(view, "MergeButton"); Program.Pump(view.Pending);
        Program.Check(Meta()!["merges"]![0]!.GetValue<string>() == "A1:B2" && view.Surface.Merges.Count == 1, "Merge cells stores the range and the grid reads it");
        view.Surface.Select(1, 1); Dispatcher.UIThread.RunJobs(); Program.Check(view.Surface.Column == 0 && view.Surface.Row == 0, "Selecting a merged cell snaps to its anchor");
        Program.Click(view, "UnmergeButton"); Program.Pump(view.Pending); Program.Check(view.Surface.Merges.Count == 0, "Unmerge removes the merge");
        view.Surface.Select(2, 3); Dispatcher.UIThread.RunJobs(); Program.Click(view, "FreezeButton"); Program.Pump(view.Pending);
        Program.Check(view.Surface.FrozenRows == 3 && view.Surface.FrozenColumns == 2, "Freeze panes above and left of the active cell");
        Program.Click(view, "UnfreezeButton"); Program.Pump(view.Pending); Program.Check(view.Surface.FrozenRows == 0 && view.Surface.FrozenColumns == 0, "Unfreeze panes");
        view.FindControl<ComboBox>("ColumnWidthBox")!.SelectedIndex = 6; Program.Pump(view.Pending); Program.Check(view.Surface.ColumnWidths.TryGetValue(2, out var width) && width == 300, "Column width applies to the selected column");
        foreach (var (a, v) in new[] { ("I1", "10"), ("I2", "2") }) Program.Pump(doc.ExecuteAsync("SetCell", new { address = a, input = v }));
        view.Surface.SelectRange(8, 0, 8, 1); Dispatcher.UIThread.RunJobs();
        view.FindControl<ComboBox>("RuleOpBox")!.SelectedIndex = 0; view.FindControl<TextBox>("RuleValueBox")!.Text = "5"; Program.Click(view, "AddRuleButton"); Program.Pump(view.Pending);
        Program.Check(view.Surface.Rules.Count == 1 && view.Surface.Rules[0].Matches(doc.Snapshot["content"]!["cells"]!["I1"]) && !view.Surface.Rules[0].Matches(doc.Snapshot["content"]!["cells"]!["I2"]), "Conditional rule matches numbers above the threshold only");
        view.FindControl<ComboBox>("ChartKindBox")!.SelectedIndex = 1; view.Surface.SelectRange(0, 0, 0, 2); Dispatcher.UIThread.RunJobs(); Program.Click(view, "InsertChartButton"); Program.Pump(view.Pending);
        Program.Check(Meta()!["charts"]!.AsArray().Count == 1 && view.FindControl<Border>("ChartPane")!.IsVisible, "Insert chart shows the chart pane");
        var sheetPath = Path.Combine(Program.Artifacts, "sheets-meta.ossheet"); Program.Pump(doc.SaveAsync(sheetPath)); using var metaReopened = Program.Get(DocumentSession.OpenAsync(engine, sheetPath));
        Program.Check(JsonNode.DeepEquals(Meta(), metaReopened.Snapshot["sheet_meta"]), "Sheet layout metadata survives save and reopen");
        Program.Pump(doc.ExecuteAsync("Undo")); Dispatcher.UIThread.RunJobs(); Program.Check(view.Surface.Rules.Count == 1 && view.FindControl<Border>("ChartPane")!.IsVisible == false, "Undo restores the previous layout");
        view.Detach(); window.Close(); Console.WriteLine("PASS Sheets functions catalog, operators, rich cell styles and extra number formats");
    }
}
