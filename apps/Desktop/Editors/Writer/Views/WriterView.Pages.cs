using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Nexolume.Desktop.Editors.Writer.Printing;

namespace Nexolume.Desktop.Editors.Writer.Views;

public partial class WriterView
{
    private void SetupPages()
    {
        this.FindControl<Button>("PageSetupButton")!.Click += async (_, _) => { await FlushAsync(); if (LastError is null) await Enqueue(ShowPageSetupAsync); };
        this.FindControl<Button>("PagePreviewButton")!.Click += async (_, _) => await ShowPrintPreviewAsync();
        this.FindControl<Button>("PageBreakButton")!.Click += async (_, _) => await ChangeStyleAsync(s => s["page_break_before"] = !(s["page_break_before"]?.GetValue<bool>() ?? false), true);
        AddHandler(KeyDownEvent, async (_, e) => { if (e.Key == Key.P && e.KeyModifiers == KeyModifiers.Control) { e.Handled = true; await ShowPrintPreviewAsync(); } }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }
    public async Task ShowPageSetupAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var dialog = new PageSetupDialog(PageSettings.Read(Document.Snapshot));
        var settings = await dialog.ShowDialog<PageSettings?>(owner);
        if (settings is not null) await Document.ExecuteAsync("SetPageLayout", settings.ToCommand());
    }
    public async Task ShowPrintPreviewAsync()
    {
        await FlushAsync(); if (LastError is not null) return;
        await Enqueue(async () =>
        {
            if (TopLevel.GetTopLevel(this) is not Window owner) return;
            var preview = new PrintPreviewWindow(new WriterPrintLayout((System.Text.Json.Nodes.JsonObject)Document.Snapshot.DeepClone()), Document.Snapshot["office_source"] is not null);
            await preview.ShowDialog(owner);
        });
    }
    private void RenderPageSettings()
    {
        var settings = PageSettings.Read(Document.Snapshot); var border = this.FindControl<Border>("EditorPage")!;
        border.Width = settings.Width; border.MinHeight = settings.Height; border.Padding = new Thickness(settings.Left, settings.Top, settings.Right, settings.Bottom);
        var zoom = this.FindControl<Slider>("ZoomSlider")!.Value / 100;
        this.FindControl<Border>("PageRuler")!.Width = settings.Width * zoom; this.FindControl<Border>("WordCompatibilityNote")!.Width = settings.Width * zoom;
        var ruler = this.FindControl<Border>("PageRuler")!.Child as Controls.PageRuler;
        if (ruler is not null) { ruler.PaperWidth = settings.Width; ruler.LeftMargin = settings.Left; ruler.RightMargin = settings.Right; ruler.InvalidateVisual(); }
        var paragraph = Document.Snapshot["content"]!["paragraphs"]!.AsArray().FirstOrDefault(p => p!["id"]!.GetValue<string>() == active);
        this.FindControl<Button>("PageBreakButton")!.Content = paragraph?["style"]?["page_break_before"]?.GetValue<bool>() == true ? "Retirer le saut de page" : "Page avant le paragraphe";
    }
}
