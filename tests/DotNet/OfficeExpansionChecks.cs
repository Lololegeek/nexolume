using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nexolume.Desktop.Editors.Sheets.Views;
using Nexolume.Desktop.Editors.Slides.Views;
using Nexolume.Desktop.Editors.Slides.Printing;
using Nexolume.Desktop.Editors.Writer.Views;
using OfficeBridge.Services;
using System.Text.Json.Nodes;

internal static class OfficeExpansionChecks
{
    public static void Writer()
    {
        using var engine = new OfficeEngine(); using var doc = Program.Get(DocumentSession.CreateAsync(engine, "writer", "Styles de rapport"));
        var view = new WriterView(doc); var window = new Window { Width = 1200, Height = 900, Content = view }; window.Show(); Dispatcher.UIThread.RunJobs();
        var id = doc.Snapshot["content"]!["paragraphs"]![0]!["id"]!.GetValue<string>();
        Program.Pump(doc.ExecuteAsync("SetParagraph", new { id, text = "Rapport personnalisé", style = new { bold = true, font_size = 24, color = "#285CB3", border = "bottom", spacing_after = 12 } }));
        var box = view.FindControl<StackPanel>("ParagraphPanel")!.GetVisualDescendants().OfType<TextBox>().First(); box.Focus(); Dispatcher.UIThread.RunJobs();
        Program.Pump(view.SavePersonalStyleAsync("Rapport")); Program.Check(view.LastError is null, "Capture personal style: " + view.LastError);
        Program.Check(doc.Snapshot["writer_styles"]!["Rapport"]!["bold"]!.GetValue<bool>(), "Style capture includes cursor formatting");
        var path = Path.Combine(Program.Artifacts, "writer-personal-styles.oswriter"); Program.Pump(doc.SaveAsync(path));
        Program.Check(JsonNode.Parse(File.ReadAllText(path))!["schema"]!.GetValue<int>() == 6, "Personal style files reject older incompatible readers");
        using var reopened = Program.Get(DocumentSession.OpenAsync(engine, path));
        Program.Check(JsonNode.DeepEquals(doc.Snapshot["writer_styles"], reopened.Snapshot["writer_styles"]), "Native style library roundtrip");
        Program.Pump(doc.ExecuteAsync("SetParagraph", new { id, style = new { bold = false, font_size = 16, color = "#111111", border = "none" } }));
        Program.Pump(view.ApplyPersonalStyleAsync("Rapport"));
        var style = doc.Snapshot["content"]!["paragraphs"]![0]!["style"]!;
        Program.Check(style["bold"]!.GetValue<bool>() && style["border"]!.GetValue<string>() == "bottom" && box.FontSize == 24, "Saved style restores paragraph and visible character formatting");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Check(box.FontSize == 16, "Personal style application undoes as one action");
        Program.Pump(doc.ExecuteAsync("Redo"));
        var library = Path.Combine(Program.Artifacts, "writer-styles.json"); Program.Pump(view.ExportPersonalStylesAsync(library));
        Program.Pump(view.DeletePersonalStyleAsync("Rapport")); Program.Check(doc.Snapshot["writer_styles"]!.AsObject().Count == 0, "Style deletion");
        Program.Pump(view.ImportPersonalStylesAsync(library)); Program.Check(view.LastError is null && doc.Snapshot["writer_styles"]!["Rapport"] is not null, "Style library import validates via Rust");
        var revision = doc.Revision; Program.Pump(view.ImportPersonalStylesAsync(library)); Program.Check(view.LastError is not null && doc.Revision == revision, "Style conflicts do not overwrite existing styles");
        var invalid = Path.Combine(Program.Artifacts, "invalid-styles.json"); File.WriteAllText(invalid, "{\"Invalid\":{\"font_size\":10000}}");
        Program.Pump(view.ImportPersonalStylesAsync(invalid)); Program.Check(view.LastError is not null && doc.Revision == revision, "Invalid imported style leaves the document intact");
        Program.Pump(view.DeletePersonalStyleAsync("Rapport")); Program.Pump(doc.ExecuteAsync("Undo")); Program.Check(doc.Snapshot["writer_styles"]!["Rapport"] is not null, "Style library edit participates in history");
        var more = view.FindControl<Button>("MoreStylesButton")!; ((Avalonia.Controls.Flyout)more.Flyout!).ShowAt(more); Dispatcher.UIThread.RunJobs();
        var personal = ((ScrollViewer)((Flyout)more.Flyout!).Content!).Content as StackPanel;
        var personalButton = personal!.Children.OfType<Button>().Single(b => b.Name == "PersonalStylesButton"); personalButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        var dialog = window.OwnedWindows.Single(); Program.Capture(dialog, "writer-personal-styles"); dialog.Close(); Dispatcher.UIThread.RunJobs();
        view.Detach(); window.Close(); Console.WriteLine("PASS Writer personal styles: capture/apply, Rust validation, atomic history, native persistence and portable libraries");
    }

    public static void Sheets()
    {
        using var engine = new OfficeEngine(); using var doc = Program.Get(DocumentSession.CreateAsync(engine, "sheets", "Recopie et échanges"));
        var view = new SheetsView(doc); var window = new Window { Width = 1440, Height = 920, Content = view }; window.Show(); Dispatcher.UIThread.RunJobs();
        string Input(string a) => doc.Snapshot["content"]!["cells"]![a]!["input"]!.GetValue<string>();
        string Display(string a) => doc.Snapshot["content"]!["cells"]![a]!["display"]!.GetValue<string>();
        foreach (var (a, value) in new[] { ("A1", "2"), ("A2", "4"), ("A3", "8"), ("B1", "10"), ("C1", "=A1*$B$1"), ("D1", "=SUM(C1:C3)") }) Program.Pump(doc.ExecuteAsync("SetCell", new { address = a, input = value }));
        Program.Pump(doc.ExecuteAsync("SetCellStyle", new { address = "C1", style = new { bold = true, format = "currency" } }));
        view.Surface.SelectRange(2, 0, 2, 2); Dispatcher.UIThread.RunJobs(); Program.Click(view, "FillDownButton"); Program.Pump(view.Pending);
        Program.Check(view.LastError is null && Input("C3") == "=A3*$B$1" && Display("D1") == "140", "Fill via ribbon shifts formulas, preserves fixed references and recalculates dependents");
        Program.Check(doc.Snapshot["content"]!["cells"]!["C3"]!["style"]!["bold"]!.GetValue<bool>(), "Fill copies formats");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Check(doc.Snapshot["content"]!["cells"]!["C3"] is null && Display("D1") == "20", "Entire fill undoes once"); Program.Pump(doc.ExecuteAsync("Redo"));
        view.Surface.SelectRange(0, 0, 0, 4); Dispatcher.UIThread.RunJobs(); Program.Click(view, "SeriesDownButton"); Program.Pump(view.Pending); Program.Check(Input("A5") == "10" && Display("D1") == "120", "Numeric series uses the two seeds and recalculates existing formulas");
        var csv = Path.Combine(Program.Artifacts, "office-import.csv"); File.WriteAllText(csv, "Nom;Texte;Valeur\r\nZoé;\"Bonjour; été\";12\r\nÉloïse;\"Deux\nLignes et \"\"guillemets\"\"\";7\r\n", new System.Text.UTF8Encoding(true));
        view.Surface.Select(5, 5); Dispatcher.UIThread.RunJobs(); Program.Pump(view.ImportCsvAsync(csv));
        Program.Check(view.LastError is null && Input("F7") == "Zoé" && Input("G8") == "Deux\nLignes et \"guillemets\"", "CSV automatic delimiter, Unicode, quotes and multiline fields");
        var exported = Path.Combine(Program.Artifacts, "office-export.csv"); Program.Pump(view.ExportCsvAsync(exported)); Program.Check(view.LastError is null, "CSV export: " + view.LastError);
        view.Surface.Select(9, 5); Dispatcher.UIThread.RunJobs(); Program.Pump(view.ImportCsvAsync(exported)); Program.Check(Input("K8") == "Deux\nLignes et \"guillemets\"" && Input("L7") == "12", "Exported UTF-8 CSV can be imported at another anchor");
        var before = doc.Revision; var badCsv = Path.Combine(Program.Artifacts, "office-invalid.csv"); File.WriteAllText(badCsv, "a;b\n\"non fermé"); Program.Pump(view.ImportCsvAsync(badCsv)); Program.Check(view.LastError is not null && doc.Revision == before, "Malformed CSV makes no partial changes");
        view.Surface.Select(13, 0); Dispatcher.UIThread.RunJobs(); Program.Pump(view.PasteTransposedAsync("a\tb\tc\r\n1\t2\t3")); Program.Check(Input("N1") == "a" && Input("O3") == "3", "Transpose swaps rows and columns");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Check(doc.Snapshot["content"]!["cells"]!["O3"] is null, "Transpose undo is atomic");
        view.Surface.SelectRange(0, 0, 2, 4); Program.Click(view, "SheetDataTab"); Program.Capture(window, "sheets-data-tools");
        var native = Path.Combine(Program.Artifacts, "office-data.ossheet"); Program.Pump(doc.SaveAsync(native)); using var reopened = Program.Get(DocumentSession.OpenAsync(engine, native)); Program.Check(JsonNode.DeepEquals(doc.Snapshot["content"], reopened.Snapshot["content"]), "Filled formulas and imports survive reopening");
        view.Detach(); window.Close(); Console.WriteLine("PASS Sheets fill/formula anchors, series, recalculation/history, CSV import/export and transpose");
    }

    public static void Slides()
    {
        using var engine = new OfficeEngine(); using var doc = Program.Get(DocumentSession.CreateAsync(engine, "slides", "Présentation exportée"));
        var view = new SlidesView(doc); var window = new Window { Width = 1440, Height = 950, Content = view }; window.Show(); Dispatcher.UIThread.RunJobs();
        Program.Pump(view.AddObjectAsync("text")); view.FindControl<TextBox>("ObjectText")!.Text = "Première diapositive — été"; Dispatcher.UIThread.RunJobs(); Program.Pump(view.FlushAsync());
        Program.Pump(view.AddObjectAsync("rectangle")); Program.Pump(view.AddSlideAsync()); Program.Pump(view.AddObjectAsync("text")); view.FindControl<TextBox>("ObjectText")!.Text = "Deuxième diapositive : conclusion"; Dispatcher.UIThread.RunJobs(); Program.Pump(view.FlushAsync());
        var slides = doc.Snapshot["content"]!["slides"]!.AsArray(); var original = slides.DeepClone();
        view.FindControl<ComboBox>("DeckThemeBox")!.SelectedIndex = 1; Program.Click(view, "ApplyDeckThemeButton"); Program.Pump(view.Pending);
        Program.Check(view.LastError is null && slides.All(s => s!["background"]!.GetValue<string>() == "#1B2A41") && slides[0]!["objects"]![0]!["fill"]!.GetValue<string>() == "#FFFFFF", "Theme recolors the complete presentation through Rust");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Check(JsonNode.DeepEquals(slides, original), "Theme undoes all slide/object changes in one step"); Program.Pump(doc.ExecuteAsync("Redo"));
        Program.Pump(view.AlignSelectedObjectAsync("right")); var obj = slides[1]!["objects"]![0]!; Program.Check(obj["x"]!.GetValue<double>() + obj["width"]!.GetValue<double>() == 960, "Right alignment reaches canvas edge");
        var path = Path.Combine(Program.Artifacts, "office-slides.osslides"); Program.Pump(doc.SaveAsync(path)); var revision = doc.Revision;
        Program.Pump(SlideExporter.PdfAsync(slides, doc.Title, Path.Combine(Program.Artifacts, "office-slides.pdf"))); Program.Pump(SlideExporter.PngAsync(slides[0]!, Path.Combine(Program.Artifacts, "office-slide.png")));
        Program.Check(doc.Revision == revision && !doc.Dirty && doc.Path == Path.GetFullPath(path), "Export preserves editable document identity and saved state");
        using (var bitmap = new Avalonia.Media.Imaging.Bitmap(Path.Combine(Program.Artifacts, "office-slide.png"))) Program.Check(bitmap.PixelSize == new Avalonia.PixelSize(1920, 1080), "PNG exports at Full HD");
        var show = new SlideshowWindow(slides); show.Show(); Dispatcher.UIThread.RunJobs();
        void Key(Key key) { show.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key }); Dispatcher.UIThread.RunJobs(); }
        Key(Avalonia.Input.Key.B); var blank = show.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PresentationBlank"); Program.Check(blank.IsVisible && blank.Background == Brushes.Black, "B blacks out the presentation");
        Key(Avalonia.Input.Key.W); Program.Check(blank.IsVisible && blank.Background == Brushes.White, "W switches to a white screen"); Key(Avalonia.Input.Key.W); Program.Check(!blank.IsVisible, "Repeated W restores the slide");
        Key(Avalonia.Input.Key.D2); Key(Avalonia.Input.Key.Enter); Program.Check(show.SlideIndex == 1, "Number and Enter navigates directly"); Key(Avalonia.Input.Key.D9); Key(Avalonia.Input.Key.Enter); Program.Check(show.SlideIndex == 1, "Invalid page number retains the current slide"); show.Close();
        Program.Capture(window, "slides-themes-export"); view.Detach(); window.Close(); Console.WriteLine("PASS Slides themes/history, alignment, PDF/PNG export, blackout and numbered slideshow navigation");
    }
}
