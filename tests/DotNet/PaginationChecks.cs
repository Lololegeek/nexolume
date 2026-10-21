using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nexolume.Desktop.Editors.Writer.Printing;
using Nexolume.Desktop.Editors.Writer.Views;
using OfficeBridge.Services;
using System.Text.Json.Nodes;

internal static class PaginationChecks
{
    public static void Run()
    {
        using var engine = new OfficeEngine(); using var doc = Program.Get(DocumentSession.CreateAsync(engine, "writer", "Mise en page Nexolume"));
        var view = new WriterView(doc); var window = new Window { Content = view, Width = 1440, Height = 1000 }; window.Show(); Dispatcher.UIThread.RunJobs();
        var id = doc.Snapshot["content"]!["paragraphs"]![0]!["id"]!.GetValue<string>();
        var text = "Titre enrichi — été 🦀\n" + string.Join(" ", Enumerable.Range(1, 220).Select(i => $"Ligne {i:D3}, le texte doit rester complet lors du passage à la page suivante."));
        Program.Pump(doc.ExecuteAsync("SetParagraph", new { id, text }));
        Program.Pump(doc.ExecuteAsync("FormatText", new { id, start = 0, end = 13, style = new { bold = true, italic = true, underline = true, font_size = 30, color = "#285CB3", highlight = "#FFF1A8" } }));
        Program.Click(view, "LayoutRibbonTab"); Program.Click(view, "PageSetupButton");
        var setup = (PageSetupDialog)window.OwnedWindows.Single(); setup.FindControl<ComboBox>("PaperSize")!.SelectedIndex = 1;
        foreach (var margin in new[] { "MarginTop", "MarginRight", "MarginBottom", "MarginLeft" }) setup.FindControl<NumericUpDown>(margin)!.Value = 1;
        Program.Capture(setup, "writer-page-setup");
        setup.FindControl<NumericUpDown>("MarginLeft")!.Value = 20; Program.Click(setup, "ApplyPageSetup");
        Program.Check(window.OwnedWindows.Contains(setup) && !string.IsNullOrWhiteSpace(setup.FindControl<TextBlock>("PageSetupError")!.Text), "Invalid margins remain in the dialog with an actionable error");
        setup.FindControl<NumericUpDown>("MarginLeft")!.Value = 1; Program.Click(setup, "ApplyPageSetup"); Program.Pump(view.Pending);
        var settings = PageSettings.Read(doc.Snapshot);
        Program.Check(Math.Abs(settings.Width - 559.37) < .1 && Math.Abs(settings.Left - 37.795) < .01, "Page dialog commits A5 dimensions and centimetre margins through Rust");
        Program.Check(Math.Abs(view.FindControl<Border>("EditorPage")!.Width - settings.Width) < .01, "Editor uses saved page width");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Check(doc.Snapshot["page_layout"] is null, "Page setup undo restores the implicit default"); Program.Pump(doc.ExecuteAsync("Redo"));
        var beforeInvalid = doc.Snapshot.DeepClone(); var rejected = false;
        try { Program.Pump(doc.ExecuteAsync("SetPageLayout", new { layout = new { width = 400, height = 400, top = 200, bottom = 200, left = 10, right = 10 } })); } catch { rejected = true; }
        Program.Check(rejected && JsonNode.DeepEquals(beforeInvalid, doc.Snapshot), "Invalid page margins are rejected without revision or snapshot mutation");
        Program.Pump(view.AddParagraphAsync()); var second = doc.Snapshot["content"]!["paragraphs"]![1]!; var secondId = second["id"]!.GetValue<string>();
        Program.Pump(doc.ExecuteAsync("SetParagraph", new { id = secondId, text = "Dernière page volontairement séparée." }));
        Program.Click(view, "PageBreakButton"); Program.Pump(view.Pending);
        var secondBox = view.FindControl<StackPanel>("ParagraphPanel")!.GetVisualDescendants().OfType<TextBox>().Last(); secondBox.Focus(); secondBox.CaretIndex = secondBox.Text!.Length; secondBox.SelectionStart = secondBox.SelectionEnd = secondBox.CaretIndex;
        window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.None); Dispatcher.UIThread.RunJobs(); Program.Pump(view.Pending);
        Program.Check(doc.Snapshot["content"]!["paragraphs"]!.AsArray().Count == 3 && doc.Snapshot["content"]!["paragraphs"]![1]!["style"]!["page_break_before"]!.GetValue<bool>() && !doc.Snapshot["content"]!["paragraphs"]![2]!["style"]!["page_break_before"]!.GetValue<bool>(), "Enter retains the break on its original paragraph without repeating it");
        Program.Pump(doc.ExecuteAsync("Undo"));
        using (var layout = new WriterPrintLayout(doc.Snapshot))
        {
            Program.Check(layout.Pages.Count > 4, "A long single paragraph is split across real output pages");
            var body = layout.Pages.SelectMany(p => p.Lines).Where(l => l.ParagraphId == id).ToArray(); var offset = 0;
            foreach (var line in body) { Program.Check(line.Text.FirstTextSourceIndex == offset, "Pagination must not skip or repeat a text range"); offset += line.Text.Length; }
            Program.Check(offset == text.Length, "All UTF16 text including Unicode and line breaks reaches the final page");
            foreach (var page in layout.Pages) foreach (var line in page.Lines)
                Program.Check(line.Origin.Y >= settings.Top - .01 && line.Origin.Y + line.Text.Height <= settings.Height - settings.Bottom + .01, "Every complete shaped line stays within vertical margins");
            Program.Check(layout.Pages[^1].Lines.All(l => l.ParagraphId == secondId) && Math.Abs(layout.Pages[^1].Lines[0].Origin.Y - settings.Top) < .01, "Explicit break starts the paragraph at the next page top");
        }
        var native = Path.Combine(Program.Artifacts, "pagination.oswriter"); Program.Pump(doc.SaveAsync(native));
        using (var reopened = Program.Get(DocumentSession.OpenAsync(engine, native))) Program.Check(JsonNode.DeepEquals(doc.Snapshot["page_layout"], reopened.Snapshot["page_layout"]) && JsonNode.DeepEquals(doc.Snapshot["content"], reopened.Snapshot["content"]), "Native reopen preserves page settings, breaks and rich text");
        Program.Check(JsonNode.Parse(File.ReadAllText(native))!["schema"]!.GetValue<int>() == 4, "Older readers cannot silently discard new page data");
        foreach (var format in new[] { "docx", "odt" })
        {
            var path = Path.Combine(Program.Artifacts, "pagination." + format); Program.Pump(doc.SaveAsync(path));
            using var reopened = Program.Get(DocumentSession.OpenAsync(engine, path)); var page = PageSettings.Read(reopened.Snapshot);
            Program.Check(Math.Abs(page.Width - settings.Width) < .1 && Math.Abs(page.Top - settings.Top) < .1 && reopened.Snapshot["content"]!["paragraphs"]![1]!["style"]!["page_break_before"]!.GetValue<bool>(), format + " roundtrip preserves dimensions, margins and page break");
        }
        var savedPath = doc.Path; var revision = doc.Revision; var dirty = doc.Dirty;
        using (var layout = new WriterPrintLayout(doc.Snapshot))
        {
            Program.Pump(layout.ExportPdfAsync(Path.Combine(Program.Artifacts, "pagination.pdf")));
            File.WriteAllText(Path.Combine(Program.Artifacts, "pagination-expected.json"), new JsonObject { ["pages"] = layout.Pages.Count, ["width_points"] = settings.Width * .75, ["height_points"] = settings.Height * .75 }.ToJsonString());
        }
        Program.Check(doc.Path == savedPath && doc.Revision == revision && doc.Dirty == dirty, "PDF export does not replace the editable document path or change its saved state");
        if (OperatingSystem.IsWindows())
        {
            using var layout = new WriterPrintLayout(doc.Snapshot); var failed = false;
            try { Program.Pump(WindowsPrinter.PrintAsync(layout, printer: "__Nexolume_missing_printer__")); } catch (System.ComponentModel.Win32Exception) { failed = true; }
            Program.Check(failed && doc.Path == savedPath && doc.Revision == revision, "An unavailable printer fails cleanly without modifying the document");
        }
        var previewTask = view.ShowPrintPreviewAsync(); Dispatcher.UIThread.RunJobs(); var preview = (PrintPreviewWindow)window.OwnedWindows.Single();
        Program.Capture(preview, "writer-print-preview"); Program.Click(preview, "NextPage");
        Program.Check(preview.FindControl<NumericUpDown>("PreviewPage")!.Value == 2, "Preview next-page navigation");
        preview.FindControl<ComboBox>("PdfPages")!.SelectedIndex = 2; preview.FindControl<NumericUpDown>("PdfFrom")!.Value = 2; preview.FindControl<NumericUpDown>("PdfTo")!.Value = 3;
        Program.Pump(preview.ExportPdfAsync(Path.Combine(Program.Artifacts, "pagination-range.pdf")));
        preview.FindControl<ComboBox>("PdfPages")!.SelectedIndex = 1; var currentPage = preview.FindControl<NumericUpDown>("PreviewPage")!; currentPage.Value = currentPage.Maximum;
        Program.Pump(preview.ExportPdfAsync(Path.Combine(Program.Artifacts, "pagination-last.pdf")));
        currentPage.Value = 2; preview.FindControl<ComboBox>("PdfPages")!.SelectedIndex = 0;
        var oldTheme = Application.Current!.RequestedThemeVariant; Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        Program.Capture(preview, "writer-print-preview-light"); Application.Current.RequestedThemeVariant = oldTheme; preview.Close(); Program.Pump(previewTask);
        // Landscape goes through the actual page dialog and affects the output geometry.
        Program.Click(view, "PageSetupButton"); setup = (PageSetupDialog)window.OwnedWindows.Single(); setup.FindControl<ComboBox>("PageOrientation")!.SelectedIndex = 1; Program.Click(setup, "ApplyPageSetup"); Program.Pump(view.Pending);
        using (var landscape = new WriterPrintLayout(doc.Snapshot)) { Program.Check(landscape.Settings.Width > landscape.Settings.Height, "Landscape swaps the paper dimensions"); Program.Pump(landscape.ExportPdfAsync(Path.Combine(Program.Artifacts, "pagination-landscape.pdf"))); }
        Program.Pump(doc.SaveAsync(Path.Combine(Program.Artifacts, "pagination-landscape.docx")));
        Program.Click(view, "PageSetupButton"); setup = (PageSetupDialog)window.OwnedWindows.Single();
        setup.FindControl<TextBox>("PageHeaderText")!.Text = "Rapport"; setup.FindControl<TextBox>("PageFooterText")!.Text = "Page {page} sur {pages}"; setup.FindControl<ComboBox>("PageFooterAlign")!.SelectedIndex = 2; Program.Click(setup, "ApplyPageSetup"); Program.Pump(view.Pending);
        Program.Check(doc.Snapshot["page_layout"]!["footer"]!.GetValue<string>() == "Page {page} sur {pages}" && doc.Snapshot["page_layout"]!["footer_align"]!.GetValue<string>() == "right" && doc.Snapshot["page_layout"]!["header"]!.GetValue<string>() == "Rapport", "Header and footer persist in the document");
        using (var withFooter = new WriterPrintLayout(doc.Snapshot)) Program.Check(withFooter.Header == "Rapport" && withFooter.Footer.Contains("{pages}"), "Print layout uses the document header and footer");
        Program.Pump(doc.SaveAsync(Path.Combine(Program.Artifacts, "pagination-footer.oswriter"))); using (var again = Program.Get(DocumentSession.OpenAsync(engine, Path.Combine(Program.Artifacts, "pagination-footer.oswriter")))) Program.Check(again.Snapshot["page_layout"]!["header"]!.GetValue<string>() == "Rapport", "Header survives native reopen");
        Program.Capture(window, "writer-page-layout"); view.Detach(); window.Close();
        Console.WriteLine("PASS Writer paper/margins dialog, undo/redo, validation, Unicode line pagination, page breaks, native/DOCX/ODT reopen, PDF export and preview navigation");
    }
}
