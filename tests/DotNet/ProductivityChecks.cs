using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Nexolume.Desktop.Editors.Writer.Views;
using Nexolume.Desktop.Editors.Sheets.Views;
using Nexolume.Desktop.Editors.Slides.Views;
using OfficeBridge.Services;

internal static class ProductivityChecks
{
    public static void Slides()
    {
        using var engine = new OfficeEngine(); using var doc = Program.Get(DocumentSession.CreateAsync(engine, "slides", "Présentation de l’équipe"));
        var view = new SlidesView(doc); var window = new Window { Content = view, Width = 1440, Height = 950 }; window.Show(); Dispatcher.UIThread.RunJobs();
        Program.Pump(view.AddLayoutAsync("title"));
        var slides = doc.Snapshot["content"]!["slides"]!.AsArray();
        Program.Check(slides.Count == 2 && slides[1]!["objects"]!.AsArray().Count == 2, "New title layout preserves existing slide");
        var id = slides[1]!["objects"]![0]!["id"]!.GetValue<string>();
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Check(slides.Count == 1, "One undo removes full layout"); Program.Pump(doc.ExecuteAsync("Redo"));
        view.FindControl<ListBox>("SlideList")!.SelectedIndex = 1; Dispatcher.UIThread.RunJobs(); view.Surface.Select(id); Dispatcher.UIThread.RunJobs();
        view.FindControl<NumericUpDown>("ObjectFontSize")!.Value = 48; Program.Pump(view.Pending);
        Program.Click(view, "ObjectBoldButton"); Program.Pump(view.Pending); Program.Click(view, "GreenFill"); Program.Pump(view.Pending);
        Program.Check(slides[1]!["objects"]![0]!["font_size"]!.GetValue<double>() == 48 && !slides[1]!["objects"]![0]!["bold"]!.GetValue<bool>(), "Native slide text styling");
        var path = System.IO.Path.Combine(Program.Artifacts, "slide-layouts.osslides"); Program.Pump(doc.SaveAsync(path)); using var reopened = Program.Get(DocumentSession.OpenAsync(engine, path));
        Program.Check(System.Text.Json.Nodes.JsonNode.DeepEquals(doc.Snapshot["content"], reopened.Snapshot["content"]), "Layout and typography native roundtrip");
        Program.Capture(window, "slides-layout-title");
        window.Width = 1000; window.Height = 720; Dispatcher.UIThread.RunJobs();
        Program.Check(!view.FindControl<Border>("InspectorPanel")!.IsVisible, "Compact view prioritizes canvas"); Program.Capture(window, "slides-layout-compact");
        Program.Click(view, "ToggleInspectorButton"); Program.Check(view.FindControl<Border>("InspectorPanel")!.IsVisible, "Inspector toggle");
        Program.Click(view, "ToggleThumbnailsButton"); Program.Check(!view.FindControl<Border>("SlideNavigator")!.IsVisible, "Thumbnail toggle");
        Program.Click(view, "SlideLayoutButton"); Dispatcher.UIThread.RunJobs();
        var layoutFlyout = (Flyout)view.FindControl<Button>("SlideLayoutButton")!.Flyout!;
        var choices = (StackPanel)layoutFlyout.Content!;
        ((Button)choices.Children[2]).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Program.Pump(view.Pending);
        Program.Check(slides.Last()!["objects"]!.AsArray().Count == 3, "Two column layout from gallery");
        view.Detach(); window.Close();
        Console.WriteLine("PASS Slides productivity: layouts, one-step undo, text typography/color and native reopen, responsive panels");
    }
    public static void Sheets()
    {
        using var engine = new OfficeEngine(); using var doc = Program.Get(DocumentSession.CreateAsync(engine, "sheets", "Budget de l’équipe"));
        var view = new SheetsView(doc); var window = new Window { Content = view, Width = 1440, Height = 950 }; window.Show(); Dispatcher.UIThread.RunJobs();
        Program.Pump(view.PasteTextAsync("Article\tQuantité\tPrix\r\nClaviers\t2\t45\r\nÉcrans\t3\t180\r\nCâbles\t6\t12\r\nTotal\t=SUM(B2:B4)\t=SUM(C2:C4)"));
        Program.Check(doc.Snapshot["content"]!["cells"]!["B5"]!["display"]!.GetValue<string>() == "11", "Paste recalculates formulas through Rust");
        Program.Check(view.Surface.Selection.Count == 15, "Pasted range selected");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Check(doc.Snapshot["content"]!["cells"]!.AsObject().Count == 0, "One undo reverts entire paste"); Program.Pump(doc.ExecuteAsync("Redo"));
        view.Surface.SelectRange(0, 0, 2, 4); Program.Pump(view.CopySelectedAsync()); view.Surface.Select(4, 0); Program.Pump(view.PasteClipboardAsync());
        Program.Check(doc.Snapshot["content"]!["cells"]!["E2"]!["input"]!.GetValue<string>() == "Claviers", "Clipboard range paste");
        Program.Pump(view.NavigateToAsync("A1:C1")); Program.Click(view, "CellBoldButton"); Program.Pump(view.Pending);
        Program.Check(new[] { "A1", "B1", "C1" }.All(a => doc.Snapshot["content"]!["cells"]![a]!["style"]!["bold"]!.GetValue<bool>()), "Range bold");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Check(!doc.Snapshot["content"]!["cells"]!["A1"]!["style"]!["bold"]!.GetValue<bool>(), "One undo reverts range formatting"); Program.Pump(doc.ExecuteAsync("Redo"));
        view.Surface.Select(0, 0); view.Surface.Focus(); window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Shift); Dispatcher.UIThread.RunJobs();
        Program.Check(view.Surface.Selection.Count == 2, "Shift arrow extends selection");
        Avalonia.Point At(double x, double y) => view.Surface.TranslatePoint(new Avalonia.Point(x, y), window)!.Value;
        window.MouseDown(At(100, 50), MouseButton.Left); window.MouseMove(At(330, 145)); window.MouseUp(At(330, 145), MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Program.Check(view.Surface.Selection.Count == 12, "Pointer drag selects a range");
        Program.Pump(view.NavigateToAsync("J1")); Program.Pump(view.PasteTextAsync("\"Texte\tavec tabulation\"\tété 🦀"));
        Program.Check(doc.Snapshot["content"]!["cells"]!["J1"]!["input"]!.GetValue<string>() == "Texte\tavec tabulation", "Quoted TSV input");
        var revision = doc.Revision; Program.Pump(view.PasteTextAsync(string.Join('\t', Enumerable.Repeat("1", 1001))));
        Program.Check(view.LastError is not null && doc.Revision == revision, "Oversized paste rejected atomically");
        Program.Pump(view.NavigateToAsync("B2:C4")); Program.Check(view.FindControl<TextBlock>("CellSummary")!.Text!.Contains("Somme"), "Selection aggregate status");
        Program.Capture(window, "sheets-ranges");
        Program.Click(view, "SheetViewTab"); view.FindControl<CheckBox>("ShowFormulasToggle")!.IsChecked = true; view.FindControl<Slider>("SheetZoomSlider")!.Value = 125;
        Program.Check(view.Surface.ShowFormulas && view.Surface.Zoom == 1.25, "Grid display and zoom");
        Program.Capture(window, "sheets-formulas"); view.Detach(); window.Close();
        Console.WriteLine("PASS Sheets productivity: pointer/keyboard ranges, clipboard TSV and Unicode, atomic paste/format undo, limits, aggregates, zoom");
    }
    public static void Writer()
    {
        using var engine = new OfficeEngine();
        using var doc = Program.Get(DocumentSession.CreateAsync(engine, "writer", "Un document structuré"));
        var id = doc.Snapshot["content"]!["paragraphs"]![0]!["id"]!.GetValue<string>();
        Program.Pump(doc.ExecuteAsync("SetParagraph", new { id, text = "Préparer la réunion", style = new { font_size = 30, bold = true } }));
        Program.Pump(doc.ExecuteAsync("InsertParagraph", new { id = "body", index = 1, text = "Les décisions et les prochaines étapes." }));
        var view = new WriterView(doc); var window = new Window { Content = view, Width = 1200, Height = 850 }; window.Show();
        Dispatcher.UIThread.RunJobs();
        Program.Click(view, "NavigationButton");
        Program.Check(view.FindControl<Border>("NavigationPanel")!.IsVisible && view.FindControl<StackPanel>("HeadingList")!.Children.OfType<Button>().Count() == 1, "Writer heading navigation");
        view.FindControl<StackPanel>("HeadingList")!.Children.OfType<Button>().Single().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Program.Check(((Grid)view.FindControl<StackPanel>("ParagraphPanel")!.Children[0]).Children.OfType<TextBox>().Single().IsFocused, "Heading navigation focuses its paragraph");
        Program.Pump(view.OrganizeParagraphAsync("body", -1));
        var panel = view.FindControl<StackPanel>("ParagraphPanel")!;
        Program.Check(((Grid)panel.Children[0]).Children.OfType<TextBox>().Single().Text == "Les décisions et les prochaines étapes.", "Displayed paragraph follows reordered model");
        Program.Pump(doc.ExecuteAsync("Undo"));
        Program.Check(((Grid)panel.Children[0]).Children.OfType<TextBox>().Single().Text == "Préparer la réunion", "Paragraph reorder undo updates display");
        Program.Pump(view.OrganizeParagraphAsync("body", 0));
        Program.Check(doc.Snapshot["content"]!["paragraphs"]!.AsArray().Count == 3, "Duplicate paragraph");
        Program.Pump(doc.ExecuteAsync("Undo"));
        Program.Click(view, "FocusModeButton");
        Program.Check(view.IsFocusMode && !view.FindControl<Border>("RibbonBorder")!.IsVisible, "Focus mode hides tools");
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); Dispatcher.UIThread.RunJobs();
        Program.Check(!view.IsFocusMode && view.FindControl<Border>("RibbonBorder")!.IsVisible, "Escape restores tools");
        Program.Click(view, "WordStatsButton"); var statistics = (Flyout)view.FindControl<Button>("WordStatsButton")!.Flyout!;
        Program.Check(((StackPanel)statistics.Content!).Children.OfType<TextBlock>().Last().Text!.Contains("Paragraphes : 2"), "Word statistics reflect document"); statistics.Hide();
        Program.Capture(window, "writer-navigation");
        view.Detach(); window.Close();
        var officePath = System.IO.Path.Combine(Program.Artifacts, "writer-ui.docx"); Program.Pump(doc.SaveAsync(officePath));
        using var imported = Program.Get(DocumentSession.OpenAsync(engine, officePath)); var importedView = new WriterView(imported);
        var importedWindow = new Window { Content = importedView, Width = 1200, Height = 850 }; importedWindow.Show(); Dispatcher.UIThread.RunJobs();
        Program.Check(importedView.FindControl<Border>("WordCompatibilityNote")!.IsVisible, "Imported Word scope is visible");
        Program.Click(importedView, "StatusFitButton"); Dispatcher.UIThread.RunJobs();
        Program.Check(importedView.FindControl<Border>("WordCompatibilityNote")!.Width == importedView.FindControl<Border>("PageRuler")!.Width, "Word notice follows fit-to-width zoom");
        Program.Capture(importedWindow, "writer-word-import"); importedView.Detach(); importedWindow.Close();
        Console.WriteLine("PASS Writer productivity: heading navigation, reorder/duplicate and undo, focus mode and Escape");
    }
}
