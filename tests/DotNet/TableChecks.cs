using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OfficeBridge.Services;
using Nexolume.Desktop.Editors.Writer.Views;
using Nexolume.Desktop.Editors.Writer.Printing;
internal static class TableChecks
{
    private static void Click(WriterView view, string name) { view.FindControl<Button>(name)!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Thread.Sleep(50); Dispatcher.UIThread.RunJobs(); Program.Pump(view.Pending); }
    public static void Writer()
    {
        using var engine = new OfficeEngine(); using var doc = Program.Get(DocumentSession.CreateAsync(engine, "writer", "Tables Writer")); var view = new WriterView(doc); var window = new Window { Content = view, Width = 1440, Height = 950 }; window.Show(); Dispatcher.UIThread.RunJobs();
        Program.Pump(view.InsertTableAsync(3, 3)); Program.Pump(view.Pending); Dispatcher.UIThread.RunJobs();
        var paragraphs = doc.Snapshot["content"]!["paragraphs"]!.AsArray();
        Program.Check(paragraphs.Count == 3 && paragraphs[1]!["table"] is not null && paragraphs[2]!["table"] is null, "Table insertion adds a table paragraph and a text paragraph after it");
        var panel = view.FindControl<StackPanel>("ParagraphPanel")!;
        Program.Check(panel.GetVisualDescendants().OfType<TextBox>().Count(b => b.Name?.StartsWith("Table_1_Cell_") == true) == 9, "Nine table cells are rendered");
        var first = panel.GetVisualDescendants().OfType<TextBox>().First(b => b.Name == "Table_1_Cell_0_0"); first.Focus(); Dispatcher.UIThread.RunJobs();
        first.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Tab }); Dispatcher.UIThread.RunJobs();
        Program.Check(panel.GetVisualDescendants().OfType<TextBox>().First(b => b.Name == "Table_1_Cell_0_1").IsFocused, "Tab moves to the next table cell");
        using (var numbered = new WriterPrintLayout(doc.Snapshot) { Footer = "Pied", Header = "Tête", NumberPages = true }) { var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(100, 140)); bitmap.Render(new PrintPageControl(numbered, 0, .1)); }
        var cell = panel.GetVisualDescendants().OfType<TextBox>().First(b => b.Name == "Table_1_Cell_1_2"); cell.Text = "3,50 €"; Dispatcher.UIThread.RunJobs();
        Program.Check(view.HasDraft, "Cell edit creates a draft"); Program.Pump(view.FlushAsync()); Dispatcher.UIThread.RunJobs();
        Program.Check(!view.HasDraft && doc.Snapshot["content"]!["paragraphs"]![1]!["table"]!["rows"]![1]![2]!.GetValue<string>() == "3,50 €", "Cell edit is committed to the engine");
        var head = panel.GetVisualDescendants().OfType<TextBox>().First(b => b.Name == "Table_1_Cell_0_0"); head.Text = "Titre"; Program.Pump(view.FlushAsync());
        panel.GetVisualDescendants().OfType<TextBox>().First(b => b.Name == "Table_1_Cell_1_1").Focus(); Dispatcher.UIThread.RunJobs();
        Program.Check(view.FindControl<Button>("TableAddRowButton")!.IsEnabled, "Table buttons are enabled with a cell focused");
        Click(view, "TableAddRowButton");
        Program.Pump(view.Pending); Dispatcher.UIThread.RunJobs();
        var rows = doc.Snapshot["content"]!["paragraphs"]![1]!["table"]!["rows"]!.AsArray();
        Program.Check(rows.Count == 4 && rows[2]![0]!.GetValue<string>() == "" && rows[1]![2]!.GetValue<string>() == "3,50 €", "Add row inserts an empty row below the active one");
        var path = Path.Combine(Program.Artifacts, "tables.oswriter"); Program.Pump(doc.SaveAsync(path)); using var reopened = Program.Get(DocumentSession.OpenAsync(engine, path));
        Program.Check(reopened.Snapshot["content"]!["paragraphs"]![1]!["table"]!["rows"]![0]![0]!.GetValue<string>() == "Titre", "Native save keeps the table");
        using (var layout = new WriterPrintLayout(doc.Snapshot)) Program.Check(layout.Pages.Sum(p => p.Cells.Count) == 12 && layout.Pages.Sum(p => p.Lines.Count) >= 3, "Print layout draws the table grid and text");
        Program.Capture(window, "writer-tables");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Pump(view.Pending); Dispatcher.UIThread.RunJobs();
        Program.Check(doc.Snapshot["content"]!["paragraphs"]![1]!["table"]!["rows"]!.AsArray().Count == 3, "Undo reverts the added row");
        Click(view, "TableDeleteButton"); Program.Pump(view.Pending); Dispatcher.UIThread.RunJobs();
        Program.Check(!doc.Snapshot["content"]!["paragraphs"]!.AsArray().Any(p => p!["table"] is not null) && !panel.GetVisualDescendants().OfType<TextBox>().Any(b => b.Name?.StartsWith("Table_") == true), "Deleting the table removes it from the editor");
        Program.Pump(view.InsertTableOfContentsAsync()); Program.Check(doc.Snapshot["content"]!["paragraphs"]!.AsArray().Count(p => p!["text"]!.GetValue<string>() == "Sommaire") == 0, "Table of contents without headings inserts nothing");
        var firstId = doc.Snapshot["content"]!["paragraphs"]![0]!["id"]!.GetValue<string>(); Program.Pump(doc.ExecuteAsync("SetParagraph", new { id = firstId, text = "Introduction", style = new { font_size = 28.0 } })); Program.Pump(view.Pending);
        var before = doc.Snapshot["content"]!["paragraphs"]!.AsArray().Count; Click(view, "InsertTocButton");
        var after = doc.Snapshot["content"]!["paragraphs"]!.AsArray(); Program.Check(after.Count == before + 2 && after[1]!["text"]!.GetValue<string>() == "Sommaire" && after[2]!["text"]!.GetValue<string>() == "Introduction", "Table of contents lists the headings in one step");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Pump(view.Pending); Program.Check(doc.Snapshot["content"]!["paragraphs"]!.AsArray().Count == before, "One undo removes the whole table of contents");
        view.Detach(); window.Close(); Console.WriteLine("PASS Writer tables: insert, edit cells, add row, native roundtrip, print layout, undo, delete, table of contents");
    }
}
