using Avalonia.Controls;
using Avalonia.Threading;
using OfficeBridge.Services;
using Nexolume.Desktop.Editors.Writer.Views;
internal static class LinkChecks
{
    private static string? LinkOf(DocumentSession doc, int index, int offset)
    {
        foreach (var run in doc.Snapshot["content"]!["paragraphs"]![index]!["runs"]?.AsArray() ?? [])
            if (offset >= run!["start"]!.GetValue<int>() && offset < run["start"]!.GetValue<int>() + run["length"]!.GetValue<int>()) return run["style"]?["link"]?.GetValue<string>();
        return null;
    }
    public static void Writer()
    {
        Program.Check(WriterView.NormalizeLink("exemple.fr") == "https://exemple.fr" && WriterView.NormalizeLink("a@b.fr") == "mailto:a@b.fr" && WriterView.NormalizeLink("javascript:alert(1)") is null && WriterView.NormalizeLink("deux mots") is null, "Link addresses are normalised and unsafe schemes rejected");
        using var engine = new OfficeEngine(); using var doc = Program.Get(DocumentSession.CreateAsync(engine, "writer", "Link Writer"));
        var view = new WriterView(doc); var window = new Window { Content = view, Width = 1440, Height = 950 }; window.Show(); Dispatcher.UIThread.RunJobs();
        var first = doc.Snapshot["content"]!["paragraphs"]![0]!["id"]!.GetValue<string>();
        Program.Pump(doc.ExecuteAsync("SetParagraph", new { id = first, text = "Visitez notre site ici" }));
        Program.Pump(doc.ExecuteAsync("InsertParagraph", new { id = Guid.NewGuid().ToString(), index = 1, text = "Second" }));
        Program.Pump(view.Pending); Dispatcher.UIThread.RunJobs();
        var panel = view.FindControl<StackPanel>("ParagraphPanel")!;
        var boxes = panel.Children.Select(c => (Nexolume.Desktop.Editors.Writer.Controls.RichParagraphBox)((Grid)c).Children[0]).ToArray();
        boxes[0].Focus(); boxes[0].SelectionStart = 8; boxes[0].SelectionEnd = 18; Dispatcher.UIThread.RunJobs();
        Program.Pump(view.SetLinkAsync("exemple.fr")); Program.Pump(view.Pending); Dispatcher.UIThread.RunJobs();
        Program.Check(LinkOf(doc, 0, 10) == "https://exemple.fr" && LinkOf(doc, 0, 2) is null or "", "Link is applied to the selection only");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Pump(view.Pending);
        Program.Check(string.IsNullOrEmpty(LinkOf(doc, 0, 10)), "One undo removes the link");
        Program.Pump(doc.ExecuteAsync("Redo")); Program.Pump(view.Pending); Dispatcher.UIThread.RunJobs();
        boxes = panel.Children.Select(c => (Nexolume.Desktop.Editors.Writer.Controls.RichParagraphBox)((Grid)c).Children[0]).ToArray();
        boxes[0].Focus(); boxes[0].SelectionStart = boxes[0].SelectionEnd = 12; Dispatcher.UIThread.RunJobs();
        Program.Pump(view.ClearLinkAsync()); Program.Pump(view.Pending);
        Program.Check(string.IsNullOrEmpty(LinkOf(doc, 0, 10)), "Removing from the caret clears the whole link");
        view.SelectAllParagraphs(); Program.Pump(view.SetLinkAsync("https://a.fr")); Program.Pump(view.Pending);
        Program.Check(LinkOf(doc, 0, 3) == "https://a.fr" && LinkOf(doc, 1, 2) == "https://a.fr", "Link applies across paragraphs");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Pump(view.Pending);
        Program.Check(string.IsNullOrEmpty(LinkOf(doc, 1, 2)), "One undo reverts the multi-paragraph link");
        boxes = panel.Children.Select(c => (Nexolume.Desktop.Editors.Writer.Controls.RichParagraphBox)((Grid)c).Children[0]).ToArray();
        boxes[0].Focus(); Dispatcher.UIThread.RunJobs();
        Program.Pump(view.ApplyNamedStyleAsync("Titre 1")); Program.Pump(view.Pending);
        var heading = doc.Snapshot["content"]!["paragraphs"]![0]!["style"]!;
        Program.Check(heading["bold"]!.GetValue<bool>() && heading["font_size"]!.GetValue<double>() == 30, "Named style Titre 1 applies size and weight");
        Program.Pump(view.FormatPainterAsync()); boxes[1].Focus(); Dispatcher.UIThread.RunJobs(); Program.Pump(view.FormatPainterAsync()); Program.Pump(view.Pending);
        Program.Check(doc.Snapshot["content"]!["paragraphs"]![1]!["style"]!["font_size"]!.GetValue<double>() == 30, "Format painter copies formatting to another paragraph");
        view.Detach(); window.Close();
        // Table picker
        using var engine2 = new OfficeEngine(); using var doc2 = Program.Get(DocumentSession.CreateAsync(engine2, "writer", "Picker"));
        var view2 = new WriterView(doc2); var window2 = new Window { Content = view2, Width = 1440, Height = 950 }; window2.Show(); Dispatcher.UIThread.RunJobs();
        var choices = view2.FontChoices("Zorglub Sans");
        Program.Check(choices[0] == "Zorglub Sans" && choices.Count >= 6 && choices.Distinct(StringComparer.OrdinalIgnoreCase).Count() == choices.Count, "Font list keeps the document font first and lists installed fonts without duplicates");
        view2.HighlightPicker(3, 4);
        var grid = view2.FindControl<Button>("InsertTableButton")!.Flyout is Flyout f && f.Content is StackPanel sp ? (Grid)sp.Children[1] : null;
        Program.Check(grid is not null && grid.Children.Count == 80, "Table picker offers an 8×10 grid");
        Program.Pump(view2.InsertTableAsync(3, 4)); Program.Pump(view2.Pending); Dispatcher.UIThread.RunJobs();
        var table = doc2.Snapshot["content"]!["paragraphs"]!.AsArray().First(p => p!["table"] is not null)!["table"]!["rows"]!.AsArray();
        Program.Check(table.Count == 3 && table[0]!.AsArray().Count == 4, "Picker size gives rows×columns table");
        view2.Detach(); window2.Close(); Console.WriteLine("PASS Writer links and table picker");
    }
}
