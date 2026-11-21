using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Nexolume.Desktop.Editors.Writer.Printing;
using Nexolume.Desktop.Editors.Writer.Spelling;

namespace Nexolume.Desktop.Platform;

/// <summary>
/// Host services that differ between the Windows desktop build and the browser build.
/// Views only talk to this interface; each head project compiles exactly one implementation
/// (<c>DesktopPlatform</c> in apps/Desktop, <c>BrowserPlatform</c> in apps/Browser).
/// </summary>
public interface IAppPlatform
{
    /// <summary>"windows" or "browser": shown in diagnostics.</summary>
    string Name { get; }
    /// <summary>True when picked files have local paths that can be reopened later (recent files, atomic replace).</summary>
    bool HasLocalFiles { get; }
    /// <summary>Label of the status bar once a document is saved.</summary>
    string SavedLocation { get; }
    string? ReadSetting(string key);
    void WriteSetting(string key, string value);
    void Log(Exception error);
    void OpenUrl(string url);
    /// <summary>Warns before the page/app is closed while documents have unsaved changes (browser beforeunload).</summary>
    void SetUnsavedChanges(bool unsaved);
    /// <summary>Whether <see cref="PrintAsync"/> can reach a printer on this host.</summary>
    bool CanPrint { get; }
    Task<bool> PrintAsync(WriterPrintLayout layout, Visual owner);
    Task<SpellingResult> CheckSpellingAsync(string text, string language);
    /// <summary>
    /// Asks where to save, then lets <paramref name="write"/> fill the file. Returns a display
    /// name (path on desktop, file name in the browser) or null when the user cancelled.
    /// Without a native save picker the browser falls back to a download.
    /// </summary>
    Task<string?> SaveFileAsync(Visual anchor, FilePickerSaveOptions options, string mime, Func<Stream, Task> write);
    /// <summary>
    /// Saves a document: to its path on desktop, to its remembered file handle or a new picked file
    /// in browsers with the File System Access API, else as a download. Returns a display name or null if cancelled.
    /// </summary>
    Task<string?> SaveDocumentAsync(Visual anchor, OfficeBridge.Services.DocumentSession document, bool saveAs, FilePickerSaveOptions options);
    /// <summary>Remembers the file a document was opened from so that "Save" can write back to it (browser).</summary>
    void RememberDocumentFile(OfficeBridge.Services.DocumentSession document, IStorageFile file);
    /// <summary>Enters or leaves full screen for a slideshow hosted in <paramref name="anchor"/>'s top level.</summary>
    void SetFullScreen(Visual anchor, bool fullScreen);
}

public static partial class AppPlatform
{
    private static IAppPlatform? current;
    /// <summary>The running host. Tests may replace it.</summary>
    public static IAppPlatform Current { get => current ??= CreateDefault(); set => current = value; }
    private static partial IAppPlatform CreateDefault();

    public static bool IsBrowser => OperatingSystem.IsBrowser();

    /// <summary>Reads a picked file into memory, refusing files larger than <paramref name="limit"/>.</summary>
    public static async Task<byte[]> ReadAllBytesAsync(IStorageFile file, long limit, string tooLarge)
    {
        await using var stream = await file.OpenReadAsync();
        if (stream.CanSeek && stream.Length > limit) throw new IOException(tooLarge);
        using var memory = new MemoryStream();
        var buffer = new byte[81920]; int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            if (memory.Length + read > limit) throw new IOException(tooLarge);
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    /// <summary>Writes a file next to its target then swaps it in, so a failure never leaves half a file.</summary>
    public static async Task WriteAtomicAsync(string path, Func<Stream, Task> write)
    {
        var target = Path.GetFullPath(path); var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = File.Create(temporary)) await write(stream);
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static TopLevel? TopLevelOf(Visual anchor) => TopLevel.GetTopLevel(anchor);
}
