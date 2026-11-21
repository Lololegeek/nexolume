using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Skia.Helpers;
using Nexolume.Desktop.Editors.Slides.Controls;
using SkiaSharp;
using System.Text.Json.Nodes;

namespace Nexolume.Desktop.Editors.Slides.Printing;

public static class SlideExporter
{
    public static Task PdfAsync(JsonArray slides, string title, string path) => Platform.AppPlatform.WriteAtomicAsync(path, stream => WritePdfAsync(slides, title, stream));
    public static Task PngAsync(JsonNode slide, string path) => Platform.AppPlatform.WriteAtomicAsync(path, stream => WritePngAsync(slide, stream));

    public static async Task WritePdfAsync(JsonArray slides, string title, Stream stream)
    {
        if (slides.Count == 0) throw new InvalidOperationException("La présentation est vide.");
        var snapshot = (JsonArray)slides.DeepClone();
        {
            using (var pdf = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { Title = title, Creator = "Nexolume" }))
            {
                foreach (var slide in snapshot)
                {
                    var visual = new ExportPage(slide!, .75); visual.Measure(new Size(720, 405)); visual.Arrange(new Rect(visual.DesiredSize));
                    var canvas = pdf.BeginPage(720, 405);
                    await DrawingContextHelper.RenderAsync(canvas, visual); pdf.EndPage(); await Task.Yield();
                }
                pdf.Close();
            }
        }
    }

    public static async Task WritePngAsync(JsonNode slide, Stream stream)
    {
        var visual = new ExportPage(slide.DeepClone(), 2); visual.Measure(new Size(1920, 1080)); visual.Arrange(new Rect(visual.DesiredSize));
        using var bitmap = new RenderTargetBitmap(new PixelSize(1920, 1080), new Vector(96, 96)); bitmap.Render(visual);
        using var buffer = new MemoryStream(); bitmap.Save(buffer); buffer.Position = 0; await buffer.CopyToAsync(stream);
    }

    private sealed class ExportPage : Control
    {
        private readonly SlideSurface surface;
        private readonly double scale;
        public ExportPage(JsonNode slide, double scale)
        {
            this.scale = scale; surface = new SlideSurface { Objects = slide["objects"]!.AsArray(), Background = slide["background"]?.GetValue<string>() ?? "#FFFFFF" };
            surface.Measure(new Size(960, 540)); surface.Arrange(new Rect(0, 0, 960, 540));
        }
        protected override Size MeasureOverride(Size availableSize) => new(960 * scale, 540 * scale);
        public override void Render(DrawingContext context)
        {
            using (context.PushTransform(Matrix.CreateScale(scale, scale)))
            using (context.PushClip(new Rect(0, 0, 960, 540))) surface.Render(context);
        }
    }
}
