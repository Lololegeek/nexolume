using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using System.Text.Json.Nodes;
namespace Nexolume.Desktop.Editors.Writer.Views;

public partial class WriterView
{
    private const long MaxImageBytes = 8 * 1024 * 1024;
    private readonly Dictionary<string, Border> imageViews = [];
    private readonly Dictionary<string, string> imageData = [];
    private string? activeImage;
    private void SetupImages()
    {
        this.FindControl<Button>("InsertImageButton")!.Click += async (_, _) => await InsertImageAsync();
    }
    private double ContentWidth { get { var page = Printing.PageSettings.Read(Document.Snapshot); return page.Width - page.Left - page.Right; } }
    private void RenderImage(StackPanel panel, int index, JsonNode p)
    {
        var id = p["id"]!.GetValue<string>(); var image = p["image"]!; var style = p["style"]!;
        if (!imageViews.TryGetValue(id, out var frame))
        {
            frame = new Border { BorderThickness = new Thickness(2), Focusable = true, Background = Brushes.White, Padding = new Thickness(2) };
            frame.PointerPressed += (_, _) => { SelectImage(id); frame.Focus(); };
            frame.KeyDown += async (_, e) => { if (e.Key is Key.Delete or Key.Back) { e.Handled = true; await DeleteImageAsync(id); } };
            var menu = new ContextMenu();
            foreach (var (label, action) in new (string, Func<Task>)[] { ("Agrandir", () => ResizeImageAsync(id, 1.25)), ("Réduire", () => ResizeImageAsync(id, .8)), ("Aligner à gauche", () => AlignImageAsync(id, "left")), ("Centrer", () => AlignImageAsync(id, "center")), ("Aligner à droite", () => AlignImageAsync(id, "right")), ("Supprimer l’image", () => DeleteImageAsync(id)) })
            { var item = new MenuItem { Header = label }; item.Click += async (_, _) => await action(); menu.Items.Add(item); }
            frame.ContextMenu = menu; frame.Name = "Image_" + index;
            imageViews[id] = frame; panel.Children.Insert(index, frame);
        }
        var current = panel.Children.IndexOf(frame); if (current != index) panel.Children.Move(current, index);
        var data = image["data"]!.GetValue<string>();
        if (!imageData.TryGetValue(id, out var shown) || shown != data)
        {
            try { frame.Child = new Image { Source = new Bitmap(new MemoryStream(Convert.FromBase64String(data))), Stretch = Stretch.Fill }; }
            catch { frame.Child = new TextBlock { Text = "Image illisible", Foreground = Brushes.Gray }; }
            imageData[id] = data;
        }
        if (frame.Child is Image view) { view.Width = image["width"]!.GetValue<double>(); view.Height = image["height"]!.GetValue<double>(); ToolTip.SetTip(view, image["alt"]!.GetValue<string>()); Avalonia.Automation.AutomationProperties.SetName(view, image["alt"]!.GetValue<string>()); }
        frame.HorizontalAlignment = style["alignment"]!.GetValue<string>() switch { "center" => HorizontalAlignment.Center, "right" => HorizontalAlignment.Right, _ => HorizontalAlignment.Left };
        frame.Margin = new Thickness(style["indent"]!.GetValue<int>() * 24, 4, 0, style["spacing_after"]!.GetValue<double>() * 4 / 3);
        frame.BorderBrush = activeImage == id ? Brush.Parse("#2F6FED") : Brushes.Transparent;
    }
    private void RemoveImageView(StackPanel panel, string id)
    {
        if (imageViews.Remove(id, out var frame)) panel.Children.Remove(frame);
        imageData.Remove(id); if (activeImage == id) activeImage = null;
    }
    private void SelectImage(string? id)
    {
        activeImage = id; if (id is not null && activeTable is not null) { activeTable = null; UpdateTableButtons(); }
        foreach (var (key, frame) in imageViews) frame.BorderBrush = key == id ? Brush.Parse("#2F6FED") : Brushes.Transparent;
        this.FindControl<Button>("DeleteParagraphButton")!.IsEnabled = id is not null || TextParagraphCount() > 1;
    }
    private int TextParagraphCount() => Document.Snapshot["content"]!["paragraphs"]!.AsArray().Count(p => p!["image"] is null && p["table"] is null);
    private JsonNode? ImageParagraph(string id) => Document.Snapshot["content"]!["paragraphs"]!.AsArray().FirstOrDefault(p => p!["id"]!.GetValue<string>() == id && p["image"] is not null);
    private async Task DeleteImageAsync(string id)
    {
        await FlushAsync(); activeImage = null;
        await Enqueue(() => Document.ExecuteAsync("DeleteParagraph", new { id }));
    }
    private async Task ResizeImageAsync(string id, double factor)
    {
        if (ImageParagraph(id) is not { } p) return; await FlushAsync();
        var image = p["image"]!.DeepClone(); var width = image["width"]!.GetValue<double>(); var height = image["height"]!.GetValue<double>();
        var floor = 16 / Math.Min(width, height); var scale = Math.Clamp(factor, floor, Math.Max(floor, ContentWidth / width));
        image["width"] = Math.Round(width * scale, 1); image["height"] = Math.Round(height * scale, 1);
        group = Guid.NewGuid().ToString();
        await Enqueue(() => Document.ExecuteAsync("SetParagraph", new { id, image }));
    }
    private async Task AlignImageAsync(string id, string alignment)
    {
        if (ImageParagraph(id) is not { } p) return; await FlushAsync();
        var style = p["style"]!.DeepClone(); style["alignment"] = alignment;
        await Enqueue(() => Document.ExecuteAsync("SetParagraph", new { id, style }));
    }
    private async Task InsertImageAsync()
    {
        var top = TopLevel.GetTopLevel(this); if (top is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new() { Title = "Insérer une image", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Images (PNG, JPEG, GIF)") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif"] }] });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            if (stream.Length > MaxImageBytes) { SetStatus("Image trop volumineuse (8 Mo maximum)."); return; }
            using var memory = new MemoryStream(); await stream.CopyToAsync(memory); await InsertImageAsync(memory.ToArray(), Path.GetFileNameWithoutExtension(files[0].Name));
        }
        catch (Exception error) { SetStatus("Impossible d’insérer l’image : " + error.Message); }
    }
    public async Task InsertImageAsync(byte[] bytes, string alt)
    {
        var mime = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 ? "image/png" : bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? "image/jpeg" : bytes.Length > 6 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' ? "image/gif" : null;
        if (mime is null) { SetStatus("Format d’image non pris en charge (PNG, JPEG ou GIF)."); return; }
        double width, height;
        using (var decoded = new Bitmap(new MemoryStream(bytes))) { width = decoded.PixelSize.Width; height = decoded.PixelSize.Height; }
        var limit = ContentWidth; if (width > limit) { height *= limit / width; width = limit; }
        var floor = 8 / Math.Min(width, height); if (floor > 1) { width *= floor; height *= floor; }
        await FlushAsync();
        var list = Document.Snapshot["content"]!["paragraphs"]!.AsArray(); var anchor = activeImage ?? active;
        var at = list.Select((p, i) => (p, i)).FirstOrDefault(x => x.p!["id"]!.GetValue<string>() == anchor).i + 1;
        var id = Guid.NewGuid().ToString(); var commands = new List<object> { new { command = "InsertParagraph", @params = new { id, index = at, text = "", image = new { mime, data = Convert.ToBase64String(bytes), width = Math.Round(width, 1), height = Math.Round(height, 1), alt } } } };
        if (at >= list.Count || list[at]!["image"] is not null) commands.Add(new { command = "InsertParagraph", @params = new { id = Guid.NewGuid().ToString(), index = at + 1, text = "" } });
        group = Guid.NewGuid().ToString();
        await Enqueue(() => Document.ExecuteAsync("Batch", new { commands = commands.ToArray() }));
        SelectImage(id);
    }
    private void SetStatus(string message) => this.FindControl<TextBlock>("WordCount")!.Text = message;
}
