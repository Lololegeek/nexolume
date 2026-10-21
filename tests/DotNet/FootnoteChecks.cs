using System.IO.Compression;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Threading;
using OfficeBridge.Services;
using Nexolume.Desktop.Editors.Writer.Views;
internal static class FootnoteChecks
{
    public static void Writer()
    {
        using var engine = new OfficeEngine(); using var doc = Program.Get(DocumentSession.CreateAsync(engine, "writer", "Notes"));
        var view = new WriterView(doc); var window = new Window { Content = view, Width = 1440, Height = 950 }; window.Show(); Dispatcher.UIThread.RunJobs();
        var first = doc.Snapshot["content"]!["paragraphs"]![0]!["id"]!.GetValue<string>();
        Program.Pump(doc.ExecuteAsync("SetParagraph", new { id = first, text = "Texte annoté" })); Program.Pump(view.Pending); Dispatcher.UIThread.RunJobs();
        Program.Click(view, "InsertFootnoteButton"); Program.Pump(view.Pending); Dispatcher.UIThread.RunJobs();
        Program.Check((doc.Snapshot["footnotes"] as JsonArray)?.Count == 1 && view.FindControl<Border>("FootnotePanel")!.IsVisible, "Inserting a footnote shows it under the page");
        var box = view.FindControl<StackPanel>("FootnoteList")!.GetVisualDescendants().OfType<TextBox>().First(); box.Text = "Source : étude 2026"; Dispatcher.UIThread.RunJobs(); Program.Pump(view.FlushAsync()); Program.Pump(view.Pending); Dispatcher.UIThread.RunJobs();
        Program.Check(doc.Snapshot["footnotes"]![0]!["text"]!.GetValue<string>() == "Source : étude 2026", "Footnote text is committed ");
        foreach (var ext in new[] { "docx", "odt" })
        {
            var path = Path.Combine(Program.Artifacts, "footnote." + ext); Program.Pump(doc.SaveAsync(path));
            using var zip = ZipFile.OpenRead(path); string Read(string name) { using var r = new StreamReader(zip.GetEntry(name)!.Open()); return r.ReadToEnd(); }
            Program.Check(ext == "docx" ? Read("word/footnotes.xml").Contains("étude 2026") && Read("word/document.xml").Contains("footnoteReference") : Read("content.xml").Contains("text:note") && Read("content.xml").Contains("étude 2026"), "Footnote exported to " + ext.ToUpperInvariant());
        }
        var native = Path.Combine(Program.Artifacts, "footnote.oswriter"); Program.Pump(doc.SaveAsync(native)); using var reopened = Program.Get(DocumentSession.OpenAsync(engine, native));
        Program.Check(JsonNode.DeepEquals(doc.Snapshot["footnotes"], reopened.Snapshot["footnotes"]), "Footnotes survive save and reopen");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Pump(view.Pending); Dispatcher.UIThread.RunJobs();
        Program.Check(doc.Snapshot["footnotes"]![0]!["text"]!.GetValue<string>() == "", "Undo reverts the note text");
        view.Detach(); window.Close(); Console.WriteLine("PASS Writer footnotes");
    }
}
