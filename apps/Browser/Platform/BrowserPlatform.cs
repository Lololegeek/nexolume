using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using OfficeBridge.Services;
using Nexolume.Desktop.Editors.Writer.Printing;
using Nexolume.Desktop.Editors.Writer.Spelling;

namespace Nexolume.Desktop.Platform;

public static partial class AppPlatform
{
    private static partial IAppPlatform CreateDefault() => new BrowserPlatform();
}

/// <summary>
/// Browser host: settings in localStorage, files through the File System Access API when the browser
/// has it (Chromium) and downloads otherwise, printing through the browser's PDF print dialog.
/// Nothing leaves the page: there is no server and no telemetry.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserPlatform : IAppPlatform
{
    private readonly ConditionalWeakTable<DocumentSession, IStorageFile> files = new();
    public string Name => "browser";
    public bool HasLocalFiles => false;
    public string SavedLocation => "Enregistré";
    public bool CanPrint => true;

    public string? ReadSetting(string key) { try { return BrowserInterop.ReadSetting(key); } catch { return null; } }
    public void WriteSetting(string key, string value)
    {
        if (!BrowserInterop.WriteSetting(key, value)) throw new IOException("Le navigateur refuse d’enregistrer les préférences (stockage plein ou désactivé).");
    }
    public void Log(Exception error) { try { BrowserInterop.LogError(error.ToString()); } catch { Console.Error.WriteLine(error); } }
    public void OpenUrl(string url)
    {
        // Only web and mail links: javascript: or data: URLs found in a document must never run in the app's origin.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto")) throw new InvalidOperationException("Seuls les liens web et e-mail peuvent être ouverts.");
        BrowserInterop.OpenUrl(uri.AbsoluteUri);
    }
    public void SetUnsavedChanges(bool unsaved) { try { BrowserInterop.SetUnsavedChanges(unsaved); } catch { } }

    public async Task<bool> PrintAsync(WriterPrintLayout layout, Visual owner)
    {
        using var pdf = new MemoryStream(); await layout.WritePdfAsync(pdf);
        BrowserInterop.PrintPdf(layout.Title, new ArraySegment<byte>(pdf.GetBuffer(), 0, (int)pdf.Length));
        return true;
    }
    public Task<SpellingResult> CheckSpellingAsync(string text, string language) =>
        Task.FromResult(new SpellingResult(false, [], "La vérification orthographique utilise le correcteur de Windows : elle n’est pas disponible dans la version navigateur."));

    public async Task<string?> SaveFileAsync(Visual anchor, FilePickerSaveOptions options, string mime, Func<Stream, Task> write)
    {
        using var memory = new MemoryStream(); await write(memory);
        if (Storage(anchor) is { CanSave: true } storage && BrowserInterop.HasSavePicker())
        {
            var file = await storage.SaveFilePickerAsync(options); if (file is null) return null;
            await WriteAsync(file, new ArraySegment<byte>(memory.GetBuffer(), 0, (int)memory.Length));
            return file.Name;
        }
        var name = options.SuggestedFileName ?? "document";
        BrowserInterop.Download(name, mime, new ArraySegment<byte>(memory.GetBuffer(), 0, (int)memory.Length));
        return name + " (téléchargé)";
    }

    public async Task<string?> SaveDocumentAsync(Visual anchor, DocumentSession document, bool saveAs, FilePickerSaveOptions options)
    {
        // "Save" writes back to the file it was opened from or last saved to, when the browser granted a writable handle.
        if (!saveAs && files.TryGetValue(document, out var known))
        {
            var revision = document.Revision; var bytes = await document.SerializeAsync(known.Name);
            try { await WriteAsync(known, bytes); document.MarkSaved(known.Name, revision); return known.Name; }
            catch (Exception ex) when (ex is not OperationCanceledException) { files.Remove(document); /* Read-only file (input element): ask where to save instead. */ }
        }
        if (Storage(anchor) is { CanSave: true } storage && BrowserInterop.HasSavePicker())
        {
            var file = await storage.SaveFilePickerAsync(options); if (file is null) return null;
            var revision = document.Revision; var bytes = await document.SerializeAsync(file.Name);
            await WriteAsync(file, bytes);
            files.AddOrUpdate(document, file); document.MarkSaved(file.Name, revision); return file.Name;
        }
        var name = options.SuggestedFileName ?? document.Title;
        var current = document.Revision; var content = await document.SerializeAsync(name);
        BrowserInterop.Download(name, Mime(name), content);
        document.MarkSaved(name, current); return name + " (téléchargé)";
    }
    public void RememberDocumentFile(DocumentSession document, IStorageFile file) => files.AddOrUpdate(document, file);
    public void SetFullScreen(Visual anchor, bool fullScreen) { try { BrowserInterop.SetFullScreen(fullScreen); } catch { } }

    private static IStorageProvider? Storage(Visual anchor) => TopLevel.GetTopLevel(anchor)?.StorageProvider;
    private static async Task WriteAsync(IStorageFile file, ArraySegment<byte> bytes)
    {
        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek) stream.SetLength(0);
        await stream.WriteAsync(bytes);
    }
    private static string Mime(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".odt" => "application/vnd.oasis.opendocument.text",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        _ => "application/json",
    };
}
