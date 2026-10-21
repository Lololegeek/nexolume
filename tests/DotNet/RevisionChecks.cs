using System.IO.Compression;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OfficeBridge.Services;
using Nexolume.Desktop.Editors.Writer.Printing;
using Nexolume.Desktop.Editors.Writer.Views;
internal static class RevisionChecks
{
    static void Settle(WriterView view) { Program.Pump(view.Pending); Dispatcher.UIThread.RunJobs(); }
    static string Zip(string path, string name) { using var zip = ZipFile.OpenRead(path); using var r = new StreamReader(zip.GetEntry(name)!.Open()); return r.ReadToEnd(); }
    static void ClickCard(WriterView view, string name) { var b = view.GetVisualDescendants().OfType<Button>().First(x => x.Name == name); Program.Check(b.IsEnabled, name + " disabled"); b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
    public static void Writer()
    {
        using var engine = new OfficeEngine(); using var doc = Program.Get(DocumentSession.CreateAsync(engine, "writer", "Relecture"));
        var view = new WriterView(doc); var window = new Window { Content = view, Width = 1440, Height = 950 }; window.Show(); Dispatcher.UIThread.RunJobs();
        var first = doc.Snapshot["content"]!["paragraphs"]![0]!["id"]!.GetValue<string>();
        Program.Pump(doc.ExecuteAsync("SetParagraph", new { id = first, text = "Texte de départ" })); Settle(view);
        // Comments
        Program.Click(view, "NewCommentButton"); Settle(view);
        Program.Check((doc.Snapshot["comments"] as JsonArray)?.Count == 1 && view.FindControl<Border>("ReviewPane")!.IsVisible, "New comment opens the review pane");
        var box = view.FindControl<StackPanel>("ReviewList")!.GetVisualDescendants().OfType<TextBox>().First(); box.Text = "À reformuler"; Dispatcher.UIThread.RunJobs(); Program.Pump(view.FlushAsync()); Settle(view);
        Program.Check(doc.Snapshot["comments"]![0]!["text"]!.GetValue<string>() == "À reformuler", "Comment text is committed");
        ClickCard(view, "ResolveComment_0"); Settle(view);
        Program.Check(doc.Snapshot["comments"]![0]!["resolved"]!.GetValue<bool>(), "Resolve marks the comment as resolved");
        foreach (var ext in new[] { "docx", "odt" })
        {
            var path = Path.Combine(Program.Artifacts, "review." + ext); Program.Pump(doc.SaveAsync(path));
            Program.Check(ext == "docx" ? Zip(path, "word/comments.xml").Contains("À reformuler") && Zip(path, "word/document.xml").Contains("commentReference") : Zip(path, "content.xml").Contains("office:annotation") && Zip(path, "content.xml").Contains("À reformuler"), "Comment exported to " + ext.ToUpperInvariant());
            using var back = Program.Get(DocumentSession.OpenAsync(engine, path));
            var imported = back.Snapshot["comments"] as JsonArray;
            Program.Check(imported?.Count == 1 && imported[0]!["text"]!.GetValue<string>() == "À reformuler" && imported[0]!["resolved"]!.GetValue<bool>(), "Comment re-imported from " + ext.ToUpperInvariant());
        }
        var native = Path.Combine(Program.Artifacts, "review.oswriter"); Program.Pump(doc.SaveAsync(native)); using var reopened = Program.Get(DocumentSession.OpenAsync(engine, native));
        Program.Check(JsonNode.DeepEquals(doc.Snapshot["comments"], reopened.Snapshot["comments"]), "Comments survive save and reopen");
        Program.Pump(doc.ExecuteAsync("Undo")); Settle(view); Program.Check(!doc.Snapshot["comments"]![0]!["resolved"]!.GetValue<bool>(), "Undo reopens the comment");
        ClickCard(view, "RemoveComment_0"); Settle(view); Program.Check((doc.Snapshot["comments"] as JsonArray ?? []).Count == 0, "Delete removes the comment");
        // Track changes
        Program.Click(view, "TrackChangesButton"); Settle(view); Program.Check(view.TrackingChanges, "Tracking turns on");
        var paragraph = view.GetVisualDescendants().OfType<Nexolume.Desktop.Editors.Writer.Controls.RichParagraphBox>().First(); paragraph.Text = "Texte final"; Dispatcher.UIThread.RunJobs(); Program.Pump(view.FlushAsync()); Settle(view);
        var revisions = doc.Snapshot["revisions"] as JsonArray;
        Program.Check(revisions?.Count == 1 && revisions[0]!["old"]!.GetValue<string>() == "Texte de départ" && revisions[0]!["kind"]!.GetValue<string>() == "edit", "A tracked edit remembers the previous text");
        foreach (var ext in new[] { "docx", "odt" })
        {
            var path = Path.Combine(Program.Artifacts, "revision." + ext); Program.Pump(doc.SaveAsync(path));
            Program.Check(ext == "docx" ? Zip(path, "word/document.xml").Contains("<w:del ") && Zip(path, "word/document.xml").Contains("<w:ins ") : Zip(path, "content.xml").Contains("text:tracked-changes"), "Tracked change exported to " + ext.ToUpperInvariant());
            using var back = Program.Get(DocumentSession.OpenAsync(engine, path));
            var imported = back.Snapshot["revisions"] as JsonArray;
            Program.Check(imported?.Count == 1 && imported[0]!["kind"]!.GetValue<string>() == "edit" && imported[0]!["old"]!.GetValue<string>() == "Texte de départ", "Tracked change re-imported from " + ext.ToUpperInvariant());
        }
        Program.Click(view, "RejectAllButton"); Settle(view);
        Program.Check(doc.Snapshot["content"]!["paragraphs"]![0]!["text"]!.GetValue<string>() == "Texte de départ" && (doc.Snapshot["revisions"] as JsonArray ?? []).Count == 0, "Reject all restores the old text");
        paragraph = view.GetVisualDescendants().OfType<Nexolume.Desktop.Editors.Writer.Controls.RichParagraphBox>().First(); paragraph.Text = "Texte revu"; Dispatcher.UIThread.RunJobs(); Program.Pump(view.FlushAsync()); Settle(view);
        Program.Click(view, "AcceptAllButton"); Settle(view);
        Program.Check(doc.Snapshot["content"]!["paragraphs"]![0]!["text"]!.GetValue<string>() == "Texte revu" && (doc.Snapshot["revisions"] as JsonArray ?? []).Count == 0, "Accept all keeps the new text");
        view.Detach(); window.Close(); Console.WriteLine("PASS Writer comments and tracked changes");
    }
    public static void Columns()
    {
        using var engine = new OfficeEngine(); using var doc = Program.Get(DocumentSession.CreateAsync(engine, "writer", "Colonnes"));
        var view = new WriterView(doc); var window = new Window { Content = view, Width = 1440, Height = 950 }; window.Show(); Dispatcher.UIThread.RunJobs();
        Program.Click(view, "LayoutRibbonTab"); Program.Click(view, "PageSetupButton");
        var setup = (PageSetupDialog)window.OwnedWindows.Single(); setup.FindControl<ComboBox>("PageColumns")!.SelectedIndex = 1; setup.FindControl<NumericUpDown>("PageColumnGap")!.Value = 0.635m; Program.Click(setup, "ApplyPageSetup"); Settle(view);
        var layout = doc.Snapshot["page_layout"]!;
        Program.Check(layout["columns"]!.GetValue<int>() == 2 && Math.Abs(layout["column_gap"]!.GetValue<double>() - 24) < .01, "Page setup stores two columns and the gap");
        foreach (var ext in new[] { "docx", "odt" })
        {
            var path = Path.Combine(Program.Artifacts, "columns." + ext); Program.Pump(doc.SaveAsync(path));
            Program.Check(ext == "docx" ? Zip(path, "word/document.xml").Contains("<w:cols w:num=\"2\"") : Zip(path, "styles.xml").Contains("fo:column-count=\"2\""), "Columns exported to " + ext.ToUpperInvariant());
            using var back = Program.Get(DocumentSession.OpenAsync(engine, path));
            Program.Check(back.Snapshot["page_layout"]?["columns"]?.GetValue<int>() == 2, "Columns re-imported from " + ext.ToUpperInvariant());
        }
        Program.Pump(doc.ExecuteAsync("Undo")); Settle(view); Program.Check(doc.Snapshot["page_layout"] is null || doc.Snapshot["page_layout"]!["columns"]!.GetValue<int>() == 1, "Undo restores one column");
        view.Detach(); window.Close(); Console.WriteLine("PASS Writer columns");
    }
}
