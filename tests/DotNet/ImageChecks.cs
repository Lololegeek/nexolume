using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OfficeBridge.Services;
using Nexolume.Desktop.Editors.Writer.Views;
using Nexolume.Desktop.Editors.Writer.Printing;
internal static class ImageChecks
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
    public static void Writer()
    {
        using var engine = new OfficeEngine(); using var doc = Program.Get(DocumentSession.CreateAsync(engine, "writer", "Images Writer")); var view = new WriterView(doc); var window = new Window { Content = view, Width = 1440, Height = 950 }; window.Show(); Dispatcher.UIThread.RunJobs();
        Program.Pump(view.InsertImageAsync(Convert.FromBase64String(Png), "Pixel")); Program.Pump(view.Pending);
        var paragraphs = doc.Snapshot["content"]!["paragraphs"]!.AsArray();
        Program.Check(paragraphs.Count == 3 && paragraphs[1]!["image"] is not null && paragraphs[2]!["image"] is null, "Image insertion adds an image paragraph and a text paragraph after it");
        var panel = view.FindControl<StackPanel>("ParagraphPanel")!; Dispatcher.UIThread.RunJobs();
        Program.Check(panel.GetVisualDescendants().OfType<Image>().Count() == 1, "Image control is rendered in the editor");
        Program.Pump(doc.ExecuteAsync("SetParagraph", new { id = paragraphs[1]!["id"]!.GetValue<string>(), image = new { mime = "image/png", data = Png, width = 200.0, height = 100.0, alt = "Pixel" } })); Program.Pump(view.Pending);
        Program.Check(Math.Abs(panel.GetVisualDescendants().OfType<Image>().First().Width - 200) < .01, "Image resize is rendered");
        var path = Path.Combine(Program.Artifacts, "images.oswriter"); Program.Pump(doc.SaveAsync(path)); using var reopened = Program.Get(DocumentSession.OpenAsync(engine, path));
        Program.Check(reopened.Snapshot["content"]!["paragraphs"]![1]!["image"]!["data"]!.GetValue<string>() == Png, "Native save keeps the image bytes");
        using (var layout = new WriterPrintLayout(doc.Snapshot)) Program.Check(layout.Pages.Sum(p => p.Pictures.Count) == 1, "Print layout places the image");
        Program.Capture(window, "writer-images");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Pump(view.Pending); Program.Pump(doc.ExecuteAsync("Undo")); Program.Pump(view.Pending);
        Program.Check(doc.Snapshot["content"]!["paragraphs"]!.AsArray().Count == 1 && !panel.GetVisualDescendants().OfType<Image>().Any(), "Undo removes the image");
        view.Detach(); window.Close(); Console.WriteLine("PASS Writer images: insert, render, resize, native roundtrip, print layout, undo");
    }
}
