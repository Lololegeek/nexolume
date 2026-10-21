using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Nexolume.Desktop.Editors.Writer.Printing;

public sealed class PrintPreviewWindow : Window
{
    private readonly WriterPrintLayout layout;
    private readonly ContentControl sheet = new();
    private readonly NumericUpDown page = new() { Name = "PreviewPage", Minimum = 1, Width = 70, Value = 1, FormatString = "0", ShowButtonSpinner = false };
    private readonly TextBlock message = new() { Name = "OutputStatus", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider zoom = new() { Minimum = 30, Maximum = 120, Value = 65, Width = 120 };
    private readonly ComboBox pdfPages = new() { Name = "PdfPages", Width = 190, ItemsSource = new[] { "Toutes les pages", "Page affichée", "Plage de pages" }, SelectedIndex = 0 };
    private readonly NumericUpDown pdfFrom = new() { Name = "PdfFrom", Width = 70, Minimum = 1, Value = 1, FormatString = "0", ShowButtonSpinner = false };
    private readonly NumericUpDown pdfTo = new() { Name = "PdfTo", Width = 70, Minimum = 1, Value = 1, FormatString = "0", ShowButtonSpinner = false };
    private readonly LayoutTransformControl transform = new();
    private readonly TextBox header = new() { Name = "PageHeader", Width = 200, Watermark = "En-tête" };
    private readonly TextBox footerText = new() { Name = "PageFooter", Width = 200, Watermark = "Pied de page" };
    private readonly CheckBox numbers = new() { Name = "PageNumbers", Content = "Numéroter les pages" };
    private bool busy;
    public PrintPreviewWindow(WriterPrintLayout layout, bool containsWordObjects = false)
    {
        this.layout = layout; Title = "Aperçu avant impression · " + layout.Title; Width = 1100; Height = 900; MinWidth = 800; MinHeight = 550; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var previous = new Button { Name = "PreviousPage", Content = "‹" }; var next = new Button { Name = "NextPage", Content = "›" };
        ToolTip.SetTip(previous, "Page précédente"); ToolTip.SetTip(next, "Page suivante");
        var export = new Button { Name = "ExportPdf", Content = "Exporter en PDF…" }; export.Classes.Add("primary");
        var print = new Button { Name = "PrintDocument", Content = "Imprimer…", IsEnabled = OperatingSystem.IsWindows() };
        var close = new Button { Content = "Fermer" }; close.Click += (_, _) => Close();
        page.Maximum = layout.Pages.Count;
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(20, 14), Children = { previous, page, new TextBlock { Text = "sur " + layout.Pages.Count, VerticalAlignment = VerticalAlignment.Center }, next, zoom, export, print, close } };
        var root = new Grid { RowDefinitions = new("Auto,Auto,*,Auto") }; root.Children.Add(new ScrollViewer { Content = tools, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        pdfFrom.Maximum = pdfTo.Maximum = layout.Pages.Count; pdfTo.Value = layout.Pages.Count;
        var range = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, IsVisible = false, Children = { new TextBlock { Text = "de", VerticalAlignment = VerticalAlignment.Center }, pdfFrom, new TextBlock { Text = "à", VerticalAlignment = VerticalAlignment.Center }, pdfTo } };
        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(20, 0, 20, 14), Children = { new TextBlock { Text = "Pages du PDF", VerticalAlignment = VerticalAlignment.Center }, pdfPages, range, header, footerText, numbers } };
        pdfPages.SelectionChanged += (_, _) => range.IsVisible = pdfPages.SelectedIndex == 2;
        Grid.SetRow(options, 1); root.Children.Add(options);
        transform.Child = sheet; transform.HorizontalAlignment = HorizontalAlignment.Center; transform.VerticalAlignment = VerticalAlignment.Top;
        var scroll = new ScrollViewer { Content = transform, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Top, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Padding = new Thickness(30), Background = Brush.Parse("#454B55") }; Grid.SetRow(scroll, 2); root.Children.Add(scroll);
        message.Margin = new Thickness(20, 12); message.Text = containsWordObjects ? "Aperçu Nexolume : les objets Word non affichés dans l’éditeur sont absents du PDF et de l’impression." : "Le PDF conserve le texte sélectionnable et les styles du document.";
        Grid.SetRow(message, 3); root.Children.Add(message); Content = root;
        var scope = new NameScope(); NameScope.SetNameScope(this, scope); foreach (var control in new Control[] { previous, next, page, message, export, print, pdfPages, pdfFrom, pdfTo, header, footerText, numbers }) scope.Register(control.Name!, control);
        header.Text = layout.Header; footerText.Text = layout.Footer;
        void Refresh() { layout.Header = header.Text ?? ""; layout.Footer = footerText.Text ?? ""; layout.NumberPages = numbers.IsChecked == true; sheet.Content = new PrintPageControl(layout, (int)(page.Value ?? 1) - 1); }
        header.TextChanged += (_, _) => Refresh(); footerText.TextChanged += (_, _) => Refresh(); numbers.IsCheckedChanged += (_, _) => Refresh();
        void RenderPage() { sheet.Content = new PrintPageControl(layout, (int)(page.Value ?? 1) - 1); previous.IsEnabled = page.Value > 1; next.IsEnabled = page.Value < layout.Pages.Count; }
        page.ValueChanged += (_, _) => { if (page.Value is decimal value && value != decimal.Truncate(value)) page.Value = decimal.Truncate(value); RenderPage(); }; previous.Click += (_, _) => page.Value--; next.Click += (_, _) => page.Value++;
        zoom.ValueChanged += (_, _) => transform.LayoutTransform = new ScaleTransform(zoom.Value / 100, zoom.Value / 100);
        transform.LayoutTransform = new ScaleTransform(.65,.65); RenderPage();
        export.Click += async (_, _) => await Output(async () =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "Exporter en PDF", SuggestedFileName = layout.Title + ".pdf", DefaultExtension = "pdf", ShowOverwritePrompt = true, FileTypeChoices = [new("Document PDF") { Patterns = ["*.pdf"] }] });
            if (file?.TryGetLocalPath() is not string path) return;
            await ExportPdfAsync(path); message.Text = "PDF exporté : " + path;
        });
        print.Click += async (_, _) => await Output(async () => { if (await WindowsPrinter.PrintAsync(layout, this)) message.Text = "Document transmis à l’imprimante."; });
        async Task Output(Func<Task> action) { if (busy) return; busy = true; export.IsEnabled = print.IsEnabled = false; try { await action(); } catch (Exception ex) { message.Text = ex.Message; } finally { busy = false; export.IsEnabled = true; print.IsEnabled = OperatingSystem.IsWindows(); } }
        Closing += (_, e) => e.Cancel = busy; Closed += (_, _) => layout.Dispose();
    }
    public Task ExportPdfAsync(string path)
    {
        static int WholePage(NumericUpDown input) => input.Value is decimal value && value == decimal.Truncate(value) ? (int)value : throw new ArgumentException("Saisissez des numéros de page entiers.");
        var from = pdfPages.SelectedIndex == 1 ? WholePage(page) : pdfPages.SelectedIndex == 2 ? WholePage(pdfFrom) : 1;
        var to = pdfPages.SelectedIndex == 1 ? from : pdfPages.SelectedIndex == 2 ? WholePage(pdfTo) : layout.Pages.Count;
        if (from > to) throw new ArgumentException("La première page doit précéder la dernière page.");
        return layout.ExportPdfAsync(path, from, to);
    }
}
