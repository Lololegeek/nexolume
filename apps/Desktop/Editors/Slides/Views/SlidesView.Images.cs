using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
namespace Nexolume.Desktop.Editors.Slides.Views;

public partial class SlidesView
{
    private const long MaxPictureBytes = 8 * 1024 * 1024;
    private void SetupImages() => this.FindControl<Button>("AddImageButton")!.Click += async (_, _) => await PickImageAsync();

    private async Task PickImageAsync()
    {
        var top = TopLevel.GetTopLevel(this); if (top is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new() { Title = "Insérer une image", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Images (PNG, JPEG, GIF)") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif"] }] });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            if (stream.Length > MaxPictureBytes) { this.FindControl<TextBlock>("ObjectKindLabel")!.Text = "Image trop volumineuse (8 Mo maximum)."; return; }
            using var memory = new MemoryStream(); await stream.CopyToAsync(memory); await AddImageAsync(memory.ToArray(), Path.GetFileNameWithoutExtension(files[0].Name));
        }
        catch (Exception error) { this.FindControl<TextBlock>("ObjectKindLabel")!.Text = "Impossible d’insérer l’image : " + error.Message; }
    }

    /// <summary>Adds a picture object centred on the slide, scaled to fit inside 70 % of it.</summary>
    public async Task AddImageAsync(byte[] bytes, string alt)
    {
        var mime = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 ? "image/png" : bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? "image/jpeg" : bytes.Length > 6 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' ? "image/gif" : null;
        if (mime is null) { this.FindControl<TextBlock>("ObjectKindLabel")!.Text = "Format d’image non pris en charge (PNG, JPEG ou GIF)."; return; }
        double width, height;
        using (var decoded = new Bitmap(new MemoryStream(bytes))) { width = decoded.PixelSize.Width; height = decoded.PixelSize.Height; }
        var scale = Math.Min(960 * .7 / width, 540 * .7 / height); var frameWidth = Math.Max(20, Math.Round(width * scale)); var frameHeight = Math.Max(20, Math.Round(height * scale));
        await FlushAsync(); var id = Guid.NewGuid().ToString();
        await Enqueue(() => Document.ExecuteAsync("AddObject", new { slide = slideId, @object = new { id, kind = "image", text = "", x = Math.Round((960 - frameWidth) / 2), y = Math.Round((540 - frameHeight) / 2), width = frameWidth, height = frameHeight, fill = "#FFFFFF", image = new { mime, data = Convert.ToBase64String(bytes), width, height, alt } } }));
        activeObject = id; Surface.Select(id); LoadProperties();
    }
}
