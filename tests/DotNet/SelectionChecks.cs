using Avalonia.Controls;
using Avalonia;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Threading;
using OfficeBridge.Services;
using Nexolume.Desktop.Editors.Writer.Views;
internal static class SelectionChecks
{
    public static void Mouse()
    {
        using var engine = new OfficeEngine(); using var doc = Program.Get(DocumentSession.CreateAsync(engine, "writer", "Sélection à la souris"));
        var first = doc.Snapshot["content"]!["paragraphs"]![0]!["id"]!.GetValue<string>();
        Program.Pump(doc.ExecuteAsync("SetParagraph", new { id = first, text = "Alpha bravo café 🦀." }));
        Program.Pump(doc.ExecuteAsync("InsertParagraph", new { id = "mouse-second", index = 1, text = "Beta mots supplémentaires." }));
        Program.Pump(doc.ExecuteAsync("InsertParagraph", new { id = "mouse-third", index = 2, text = "Gamma dernier paragraphe." }));
        var view = new WriterView(doc); var window = new Window { Content = view, Width = 1200, Height = 850 }; window.Show(); Dispatcher.UIThread.RunJobs();
        var panel = view.FindControl<StackPanel>("ParagraphPanel")!;
        var boxes = panel.GetVisualDescendants().OfType<Nexolume.Desktop.Editors.Writer.Controls.RichParagraphBox>().ToArray();
        Point At(int paragraph, int offset)
        {
            window.UpdateLayout();
            var presenter = boxes[paragraph].GetVisualDescendants().OfType<TextPresenter>().First();
            var rect = presenter.TextLayout.HitTestTextPosition(offset);
            return presenter.TranslatePoint(new Point(rect.X + .1, rect.Y + rect.Height / 2), window)!.Value;
        }
        void Drag(int a, int from, int b, int to)
        {
            window.MouseDown(At(a, from), MouseButton.Left); window.MouseMove(At(b, to), Avalonia.Input.RawInputModifiers.LeftMouseButton); window.MouseUp(At(b, to), MouseButton.Left); Dispatcher.UIThread.RunJobs();
        }
        Drag(0, 2, 2, 4);
        Program.Check(view.HasMultiSelection && view.SelectedText() == "pha bravo café 🦀.\nBeta mots supplémentaires.\nGamm", "Real pointer drag must select exact text across paragraph presenters: " + view.SelectedText() + " / " + string.Join(";", boxes.Select(b => $"{b.SelectionStart}:{b.SelectionEnd}")));
        Drag(2, 4, 0, 2); Program.Check(view.SelectedText() == "pha bravo café 🦀.\nBeta mots supplémentaires.\nGamm", "Reverse mouse selection preserves the anchor and exact endpoints");
        window.MouseDown(At(0, 2), MouseButton.Left); window.MouseMove(At(2, 3), Avalonia.Input.RawInputModifiers.LeftMouseButton); window.MouseMove(At(0, 8), Avalonia.Input.RawInputModifiers.LeftMouseButton); window.MouseUp(At(0, 8), MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Program.Check(!view.HasMultiSelection && boxes[0].SelectedText == "pha br" && boxes[1].SelectionStart == boxes[1].SelectionEnd && boxes[2].SelectionStart == boxes[2].SelectionEnd, "Dragging back into the anchor paragraph keeps the local range and clears the others");
        view.FindControl<Slider>("ZoomSlider")!.Value = 75;
        Program.Pump(doc.ExecuteAsync("SetParagraph", new { id = first, style = new { list = "bullet", indent = 2, font_size = 24 } }));
        Program.Pump(doc.ExecuteAsync("FormatText", new { id = first, start = 3, end = 8, style = new { bold = true, font_size = 32 } })); Dispatcher.UIThread.RunJobs();
        Drag(0, 2, 2, 4); Program.Check(view.SelectedText() == "pha bravo café 🦀.\nBeta mots supplémentaires.\nGamm", "Mouse hit testing uses zoomed rich text, indentation and list layout");
        Program.Capture(window, "writer-mouse-selection");
        view.Detach(); window.Close(); Console.WriteLine("PASS real Writer mouse selection: forward/reverse, returning to anchor, zoom and mixed text/list layout");
    }
    private static void Key(Control target, Key key, KeyModifiers modifiers = KeyModifiers.None) { target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers }); Dispatcher.UIThread.RunJobs(); Thread.Sleep(50); Dispatcher.UIThread.RunJobs(); }
    private static string[] Texts(DocumentSession doc) => doc.Snapshot["content"]!["paragraphs"]!.AsArray().Select(p => p!["text"]!.GetValue<string>()).ToArray();
    public static void Writer()
    {
        using var engine = new OfficeEngine(); using var doc = Program.Get(DocumentSession.CreateAsync(engine, "writer", "Selection Writer"));
        var view = new WriterView(doc); var window = new Window { Content = view, Width = 1440, Height = 950 }; window.Show(); Dispatcher.UIThread.RunJobs();
        var first = doc.Snapshot["content"]!["paragraphs"]![0]!["id"]!.GetValue<string>();
        Program.Pump(doc.ExecuteAsync("SetParagraph", new { id = first, text = "Alpha" }));
        Program.Pump(doc.ExecuteAsync("InsertParagraph", new { id = Guid.NewGuid().ToString(), index = 1, text = "Beta" }));
        Program.Pump(doc.ExecuteAsync("InsertParagraph", new { id = Guid.NewGuid().ToString(), index = 2, text = "Gamma" }));
        Program.Pump(view.Pending); Dispatcher.UIThread.RunJobs();
        var panel = view.FindControl<StackPanel>("ParagraphPanel")!;
        var boxes = panel.Children.Select(c => (Nexolume.Desktop.Editors.Writer.Controls.RichParagraphBox)((Grid)c).Children[0]).ToArray();
        boxes[0].Focus(); Dispatcher.UIThread.RunJobs();
        Key(boxes[0], Avalonia.Input.Key.A, KeyModifiers.Control);
        Program.Check(view.HasMultiSelection && view.SelectedText() == "Alpha\nBeta\nGamma", "Ctrl+A selects every paragraph");
        Program.Check(boxes.All(b => b.SelectionStart != b.SelectionEnd), "Every paragraph box shows its part of the selection");
        Key(boxes[2], Avalonia.Input.Key.B, KeyModifiers.Control); Program.Pump(view.Pending); Dispatcher.UIThread.RunJobs();
        Program.Check(boxes.All(b => b.StyleAt(1)["bold"]!.GetValue<bool>()), "Bold applies to the whole multi-paragraph selection");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Pump(view.Pending); Dispatcher.UIThread.RunJobs();
        Program.Check(!boxes[1].StyleAt(1)["bold"]!.GetValue<bool>(), "One undo removes the bold from every paragraph");
        boxes[1].Focus(); boxes[1].SelectionStart = 2; boxes[1].SelectionEnd = 2; Dispatcher.UIThread.RunJobs();
        Key(boxes[1], Avalonia.Input.Key.Escape);
        view.SelectRange(0, 2, 2, 3); Dispatcher.UIThread.RunJobs();
        Program.Check(view.SelectedText() == "pha\nBeta\nGam", "A partial range reads across paragraphs");
        Program.Pump(view.DeleteMultiAsync("")); Program.Pump(view.Pending); Dispatcher.UIThread.RunJobs();
        Program.Check(Texts(doc).SequenceEqual(["Alma"]) && !view.HasMultiSelection, "Deleting a partial range merges the ends in one paragraph");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Pump(view.Pending); Dispatcher.UIThread.RunJobs();
        Program.Check(Texts(doc).SequenceEqual(["Alpha", "Beta", "Gamma"]), "One undo restores all three paragraphs");
        Program.Pump(doc.ExecuteAsync("SetParagraph", new { id = doc.Snapshot["content"]!["paragraphs"]![0]!["id"]!.GetValue<string>(), text = "Zèbre" })); Program.Pump(view.Pending);
        Program.Pump(doc.ExecuteAsync("InsertParagraph", new { id = Guid.NewGuid().ToString(), index = 3, text = "" })); Program.Pump(view.Pending); Dispatcher.UIThread.RunJobs();
        Program.Pump(view.RemoveEmptyParagraphsAsync()); Program.Check(Texts(doc).SequenceEqual(["Zèbre", "Beta", "Gamma"]), "Remove empty paragraphs keeps the text ones");
        Program.Pump(view.SortParagraphsAsync()); Program.Check(Texts(doc).SequenceEqual(["Beta", "Gamma", "Zèbre"]), "Sort A→Z reorders paragraphs");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Pump(view.Pending); Program.Check(Texts(doc).SequenceEqual(["Zèbre", "Beta", "Gamma"]), "One undo reverts the sort");
        view.SelectAllParagraphs(); Program.Pump(view.ReverseParagraphsAsync()); Program.Check(Texts(doc).SequenceEqual(["Gamma", "Beta", "Zèbre"]), "Reverse swaps selected paragraphs");
        view.SelectAllParagraphs(); Program.Pump(view.JoinParagraphsAsync()); Dispatcher.UIThread.RunJobs(); Program.Check(Texts(doc).SequenceEqual(["Gamma Beta Zèbre"]), "Join merges selected paragraphs");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Pump(view.Pending); Program.Check(Texts(doc).Length == 3, "One undo reverts the join");
        view.Detach(); window.Close(); Console.WriteLine("PASS Writer selection: select all, multi-paragraph bold, delete across paragraphs, one undo, sort, remove empty");
    }
}
