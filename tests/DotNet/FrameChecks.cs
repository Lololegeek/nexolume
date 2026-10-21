using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using OfficeBridge.Services;
using Nexolume.Desktop.Editors.Writer.Views;
using Nexolume.Desktop.Editors.Writer.Printing;
internal static class FrameChecks
{
    public static void Writer()
    {
        using var engine = new OfficeEngine(); using var doc = Program.Get(DocumentSession.CreateAsync(engine, "writer", "Frame Writer"));
        var view = new WriterView(doc); var window = new Window { Content = view, Width = 1440, Height = 950 }; window.Show(); Dispatcher.UIThread.RunJobs();
        var first = doc.Snapshot["content"]!["paragraphs"]![0]!["id"]!.GetValue<string>();
        Program.Pump(doc.ExecuteAsync("SetParagraph", new { id = first, text = "Encadré" })); Program.Pump(view.Pending); Dispatcher.UIThread.RunJobs();
        var box = (Nexolume.Desktop.Editors.Writer.Controls.RichParagraphBox)((Grid)view.FindControl<StackPanel>("ParagraphPanel")!.Children[0]).Children[0];
        box.Focus(); Dispatcher.UIThread.RunJobs();
        void Pick(string name, string tag) { var combo = view.FindControl<ComboBox>(name)!; combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag! == tag); Program.Pump(view.Pending); Dispatcher.UIThread.RunJobs(); }
        Pick("ShadingBox", "#FFF2CC"); Pick("BorderBox", "box");
        var style = doc.Snapshot["content"]!["paragraphs"]![0]!["style"]!;
        Program.Check(style["shading"]!.GetValue<string>() == "#FFF2CC" && style["border"]!.GetValue<string>() == "box", "Shading and border reach the engine");
        Program.Check(box.BorderThickness.Left == 1 && box.Background is ISolidColorBrush { Color: var c } && c == Color.Parse("#FFF2CC"), "The editor draws the shading and the frame");
        using var layout = new WriterPrintLayout(doc.Snapshot);
        Program.Check(layout.Pages[0].Frames.Count == 1 && layout.Pages[0].Frames[0].Border == "box", "Print layout carries the frame");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Pump(doc.ExecuteAsync("Undo")); Program.Pump(view.Pending); Dispatcher.UIThread.RunJobs();
        Program.Check(box.BorderThickness.Left == 0, "Undo removes the frame");
        view.Detach(); window.Close(); Console.WriteLine("PASS Writer shading and borders: ribbon, editor, print, undo");
    }
}
