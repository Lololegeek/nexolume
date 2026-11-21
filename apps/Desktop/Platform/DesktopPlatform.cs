using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Nexolume.Desktop.Editors.Writer.Printing;
using Nexolume.Desktop.Editors.Writer.Spelling;

namespace Nexolume.Desktop.Platform;

public static partial class AppPlatform
{
    private static partial IAppPlatform CreateDefault() => new DesktopPlatform();
}

/// <summary>Windows desktop: settings in %LOCALAPPDATA%\Nexolume, real paths, Windows printing and spell checking.</summary>
public sealed class DesktopPlatform : IAppPlatform
{
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexolume");
    public string Name => "windows";
    public bool HasLocalFiles => true;
    public string SavedLocation => "Enregistré sur cet ordinateur";
    public bool CanPrint => OperatingSystem.IsWindows();

    public string? ReadSetting(string key) { try { return File.ReadAllText(Path.Combine(Folder, key + ".json")); } catch { return null; } }
    public void WriteSetting(string key, string value)
    {
        Directory.CreateDirectory(Folder); var path = Path.Combine(Folder, key + ".json");
        File.WriteAllText(path + ".tmp", value); File.Move(path + ".tmp", path, true);
    }
    public void Log(Exception error) { try { Directory.CreateDirectory(Folder); File.AppendAllText(Path.Combine(Folder, "errors.log"), $"{DateTimeOffset.Now:O} {error}\n"); } catch { } }
    public void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    public void SetUnsavedChanges(bool unsaved) { /* The main window asks before closing. */ }
    public Task<bool> PrintAsync(WriterPrintLayout layout, Visual owner) =>
        TopLevel.GetTopLevel(owner) is Window window ? WindowsPrinter.PrintAsync(layout, window) : Task.FromResult(false);
    // Each call owns and releases all its COM objects on the same background thread.
    public Task<SpellingResult> CheckSpellingAsync(string text, string language) => Task.Run(() => WindowsSpelling.Check(text, language));

    public async Task<string?> SaveFileAsync(Visual anchor, FilePickerSaveOptions options, string mime, Func<Stream, Task> write)
    {
        if (TopLevel.GetTopLevel(anchor)?.StorageProvider is not { } storage) return null;
        var file = await storage.SaveFilePickerAsync(options); if (file is null) return null;
        var path = file.TryGetLocalPath() ?? throw new IOException("Ce dossier n’est pas accessible.");
        await AppPlatform.WriteAtomicAsync(path, write); return path;
    }
    public async Task<string?> SaveDocumentAsync(Visual anchor, OfficeBridge.Services.DocumentSession document, bool saveAs, FilePickerSaveOptions options)
    {
        var path = saveAs ? null : document.Path;
        if (path is null)
        {
            if (TopLevel.GetTopLevel(anchor)?.StorageProvider is not { } storage) return null;
            var file = await storage.SaveFilePickerAsync(options); if (file is null) return null;
            path = file.TryGetLocalPath() ?? throw new IOException("Ce dossier n’est pas accessible.");
        }
        await document.SaveAsync(path); return path;
    }
    public void RememberDocumentFile(OfficeBridge.Services.DocumentSession document, IStorageFile file) { /* Desktop documents keep their path. */ }
    public void SetFullScreen(Visual anchor, bool fullScreen) { if (TopLevel.GetTopLevel(anchor) is Window window) window.WindowState = fullScreen ? WindowState.FullScreen : WindowState.Normal; }
}
