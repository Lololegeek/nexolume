using Avalonia.Controls;
using Avalonia.Platform.Storage;
using OfficeBridge.Services;
using Nexolume.Desktop.Platform;
namespace Nexolume.Desktop.Shell;

public partial class MainView
{
    private static readonly FilePickerFileType OfficeFiles = new("Documents Nexolume, Word, Excel, PowerPoint et LibreOffice") { Patterns = ["*.oswriter", "*.ossheet", "*.osslides", "*.docx", "*.odt", "*.xlsx", "*.pptx"], MimeTypes = ["application/json", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/vnd.oasis.opendocument.text", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "application/vnd.openxmlformats-officedocument.presentationml.presentation"] };
    // Native documents may reach 16 MiB, DOCX/ODT/XLSX/PPTX 10 MiB (checked again by DocumentSession).
    private const long OpenLimit = 16L * 1024 * 1024;

    private async Task OpenPickerAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new() { Title = "Ouvrir un document", AllowMultiple = true, FileTypeFilter = [OfficeFiles] });
        foreach (var file in files) await OpenFileAsync(file);
    }

    /// <summary>Opens a picked or dropped file: by path on desktop, from its bytes in the browser.</summary>
    public async Task OpenFileAsync(IStorageFile file)
    {
        if (AppPlatform.Current.HasLocalFiles && file.TryGetLocalPath() is { } path) { await OpenAsync(path); return; }
        var bytes = await AppPlatform.ReadAllBytesAsync(file, OpenLimit, "Le fichier dépasse la limite de 16 Mio.");
        var doc = await DocumentSession.OpenAsync(engine, file.Name, bytes);
        try { AddDocument(doc); AppPlatform.Current.RememberDocumentFile(doc, file); SetStatus("Ouvert : " + file.Name); }
        catch { doc.Dispose(); throw; }
    }

    public async Task OpenAsync(string path) { var doc = await DocumentSession.OpenAsync(engine, path); try { AddDocument(doc); preferences.Remember(path); home.SetRecent(preferences.Recent); } catch { doc.Dispose(); throw; } }
    private async Task SaveCurrentAsync(bool saveAs, string? format = null) { if (Current is { } view) await SaveViewAsync(view, saveAs, format); }

    private async Task<bool> SaveViewAsync(Editors.EditorView view, bool saveAs, string? format = null)
    {
        await view.FlushAsync(); if (view.LastError is not null) throw new InvalidOperationException("Corrigez la dernière modification avant d’enregistrer.", view.LastError);
        var doc = view.Document;
        var native = doc.Kind switch { "writer" => "oswriter", "sheets" => "ossheet", _ => "osslides" };
        var extension = format ?? (doc.FileName is { } name && Path.GetExtension(name).Length > 1 ? Path.GetExtension(name)[1..].ToLowerInvariant() : native);
        var choices = doc.Kind switch
        {
            "writer" => new[] { new FilePickerFileType("Document Nexolume") { Patterns = ["*.oswriter"] }, new FilePickerFileType("Word (.docx)") { Patterns = ["*.docx"] }, new FilePickerFileType("OpenDocument (.odt)") { Patterns = ["*.odt"] } },
            "sheets" => [new FilePickerFileType("Classeur Nexolume") { Patterns = ["*.ossheet"] }, new FilePickerFileType("Excel (.xlsx)") { Patterns = ["*.xlsx"] }],
            _ => [new FilePickerFileType("Présentation Nexolume") { Patterns = ["*.osslides"] }, new FilePickerFileType("PowerPoint (.pptx)") { Patterns = ["*.pptx"] }],
        };
        // The chosen format goes first so that browsers without a format list still use it.
        choices = choices.OrderByDescending(c => c.Patterns?.Contains("*." + extension) == true).ToArray();
        var options = new FilePickerSaveOptions { Title = "Enregistrer le document", SuggestedFileName = doc.Title + "." + extension, DefaultExtension = extension, ShowOverwritePrompt = true, FileTypeChoices = choices };
        var saved = await AppPlatform.Current.SaveDocumentAsync(this, doc, saveAs || format is not null, options);
        if (saved is null) return false;
        if (doc.Path is { } path) { preferences.Remember(path); home.SetRecent(preferences.Recent); }
        SetStatus("Enregistré : " + saved); AppPlatform.Current.SetUnsavedChanges(HasUnsavedDocuments); return true;
    }

    public async Task<bool> CloseTabAsync(TabItem tab)
    {
        if (!editors.TryGetValue(tab, out var view)) return true; await view.FlushAsync(); if (view.LastError is not null) throw new InvalidOperationException("Corrigez la dernière modification avant de fermer.", view.LastError);
        if (view.Document.Dirty) { var result = await ConfirmCloseAsync(view.Document.Title); if (result == "cancel") return false; if (result == "save" && !await SaveViewAsync(view, false)) return false; }
        view.Detach(); view.Document.Dispose(); editors.Remove(tab); tabs.Items.Remove(tab); if (tabs.SelectedItem is null) tabs.SelectedItem = homeTab; UpdateChrome();
        AppPlatform.Current.SetUnsavedChanges(HasUnsavedDocuments); return true;
    }
    private async Task<string> ConfirmCloseAsync(string title) => await new CloseDocumentDialog(title).ShowModalAsync<string>(this) ?? "cancel";
}
