using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OfficeBridge.Services;
using Nexolume.Desktop.Editors.Writer.Views;
internal static class OfficeFeatureChecks
{
    public static void Until(Func<bool> ready) { var deadline = DateTime.UtcNow.AddSeconds(10); while (!ready()) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(2); if (DateTime.UtcNow > deadline) throw new TimeoutException("UI operation did not complete"); } Dispatcher.UIThread.RunJobs(); }
    public static void Writer()
    {
        using var engine = new OfficeEngine(); using var doc = Program.Get(DocumentSession.CreateAsync(engine, "writer", "Fonctions Writer")); var view = new WriterView(doc); var window = new Window { Content = view, Width = 1440, Height = 950 }; window.Show(); Dispatcher.UIThread.RunJobs();
        var panel = view.FindControl<StackPanel>("ParagraphPanel")!; TextBox Box() => panel.GetVisualDescendants().OfType<TextBox>().First(); var box = Box(); box.Focus(); window.KeyTextInput("Bonjour 🦀 monde"); Dispatcher.UIThread.RunJobs(); Program.Pump(view.FlushAsync()); box.CaretIndex = 10; box.SelectionStart = 10; box.SelectionEnd = 10; window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Until(() => doc.Snapshot["content"]!["paragraphs"]!.AsArray().Count == 2); Program.Pump(view.Pending); var second = panel.GetVisualDescendants().OfType<TextBox>().Last(); Program.Check(second.Text == " monde", "Enter splits UTF-16 text in Rust"); second.Focus(); second.CaretIndex = 0; second.SelectionStart = second.SelectionEnd = 0; window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None); Until(() => doc.Snapshot["content"]!["paragraphs"]!.AsArray().Count == 1); Program.Pump(view.Pending);
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Check(doc.Snapshot["content"]!["paragraphs"]!.AsArray().Count == 2, "Merge undo"); Program.Pump(doc.ExecuteAsync("Redo")); box = Box(); box.Focus(); Program.Click(view, "BulletButton"); Program.Pump(view.Pending); Program.Check(doc.Snapshot["content"]!["paragraphs"]![0]!["style"]!["list"]!.GetValue<string>() == "bullet", "Bullet list stored in Rust");
        var colors = view.FindControl<ComboBox>("FontColorBox")!; colors.SelectedIndex = 1; Program.Pump(view.Pending); Program.Check(box.Foreground!.ToString()!.Equals("#ff285cb3", StringComparison.OrdinalIgnoreCase), "Text color rendered");
        view.FindControl<ComboBox>("LineSpacingBox")!.SelectedIndex = 3; Program.Pump(view.Pending); Program.Check(Math.Abs(box.LineHeight - box.FontSize * 2) < .01, "Line spacing rendered");
        var find = view.FindControl<TextBox>("FindInput")!; find.Text = "monde"; Dispatcher.UIThread.RunJobs(); Program.Pump(view.FindNextAsync()); Program.Check(box.SelectedText == "monde", "Find selects actual occurrence"); view.FindControl<TextBox>("ReplaceInput")!.Text = "Nexolume"; Dispatcher.UIThread.RunJobs(); Program.Click(view, "ReplaceAllButton"); Program.Pump(view.Pending); Program.Check(box.Text == "Bonjour 🦀 Nexolume", "Replace all updates native model and UI"); Program.Pump(doc.ExecuteAsync("Undo")); Program.Check(box.Text == "Bonjour 🦀 monde", "Replace all single undo");
        var path = Path.Combine(Program.Artifacts, "office-writer.oswriter"); Program.Pump(doc.SaveAsync(path)); using var reopened = Program.Get(DocumentSession.OpenAsync(engine, path)); Program.Check(System.Text.Json.Nodes.JsonNode.DeepEquals(doc.Snapshot["content"], reopened.Snapshot["content"]), "Extended Writer format roundtrip");
        Program.Capture(window, "writer-office-features"); view.Detach(); window.Close(); Console.WriteLine("PASS Office Writer: Unicode Enter/Backspace, undo, lists, colors, line spacing, find/replace, native roundtrip");
    }
}
