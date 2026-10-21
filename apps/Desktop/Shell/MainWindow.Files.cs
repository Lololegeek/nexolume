using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using OfficeBridge.Services;
namespace Nexolume.Desktop.Shell;

public partial class MainWindow
{
    private static readonly FilePickerFileType OfficeFiles = new("Documents Nexolume, Word et LibreOffice") { Patterns = ["*.oswriter", "*.ossheet", "*.osslides", "*.docx", "*.odt"] };
    private async Task OpenPickerAsync() { var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Ouvrir un document", AllowMultiple = true, FileTypeFilter = [OfficeFiles] }); foreach (var file in files) { var path = file.TryGetLocalPath(); if (path is not null) await OpenAsync(path); } }
    public async Task OpenAsync(string path) { var doc = await DocumentSession.OpenAsync(engine, path); try { AddDocument(doc); preferences.Remember(path); home.SetRecent(preferences.Recent); } catch { doc.Dispose(); throw; } }
    private async Task SaveCurrentAsync(bool saveAs) { if (Current is { } view) await SaveViewAsync(view, saveAs); }
    private async Task<bool> SaveViewAsync(Editors.EditorView view, bool saveAs)
    {
        await view.FlushAsync(); if (view.LastError is not null) throw new InvalidOperationException("Corrigez la dernière modification avant d’enregistrer.", view.LastError); var doc = view.Document; string? path = saveAs ? null : doc.Path;
        if (path is null) { var extension = doc.Kind switch { "writer" => "oswriter", "sheets" => "ossheet", _ => "osslides" }; var choices = doc.Kind == "writer" ? new[] { new FilePickerFileType("Document Nexolume") { Patterns = ["*.oswriter"] }, new FilePickerFileType("Word (.docx)") { Patterns = ["*.docx"] }, new FilePickerFileType("OpenDocument (.odt)") { Patterns = ["*.odt"] } } : [new FilePickerFileType("Document " + doc.Kind) { Patterns = ["*." + extension] }]; var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "Enregistrer le document", SuggestedFileName = doc.Title + "." + extension, DefaultExtension = extension, ShowOverwritePrompt = true, FileTypeChoices = choices }); if (file is null) return false; path = file.TryGetLocalPath(); if (path is null) throw new IOException("Ce dossier n’est pas accessible."); }
        await doc.SaveAsync(path); preferences.Remember(path); home.SetRecent(preferences.Recent); this.FindControl<TextBlock>("StatusText")!.Text = "Enregistré : " + path; return true;
    }
    public async Task<bool> CloseTabAsync(TabItem tab)
    {
        if (!editors.TryGetValue(tab, out var view)) return true; await view.FlushAsync(); if (view.LastError is not null) throw new InvalidOperationException("Corrigez la dernière modification avant de fermer.", view.LastError);
        if (view.Document.Dirty) { var result = await ConfirmCloseAsync(view.Document.Title); if (result == "cancel") return false; if (result == "save" && !await SaveViewAsync(view, false)) return false; }
        view.Detach(); view.Document.Dispose(); editors.Remove(tab); tabs.Items.Remove(tab); if (tabs.SelectedItem is null) tabs.SelectedItem = homeTab; UpdateChrome(); return true;
    }
    private async Task<string> ConfirmCloseAsync(string title)
    {
        var dialog = new CloseDocumentDialog(title); return await dialog.ShowDialog<string>(this) ?? "cancel";
    }
}
