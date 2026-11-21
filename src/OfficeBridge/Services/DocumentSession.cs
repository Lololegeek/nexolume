using System.Text.Json.Nodes;
using OfficeBridge.Deltas;
using OfficeBridge.Models;
namespace OfficeBridge.Services;

public sealed class DocumentSession : IDisposable
{
    private readonly OfficeEngine engine;
    private readonly SemaphoreSlim gate = new(1, 1);
    public JsonObject Snapshot { get; private set; }
    public string Id => Snapshot["id"]!.GetValue<string>();
    public long Revision => Snapshot["revision"]!.GetValue<long>();
    public string Title => Snapshot["title"]!.GetValue<string>();
    public string Kind => Snapshot["content"]!["kind"]!.GetValue<string>();
    public string? Path { get; private set; }
    public bool Dirty { get; private set; }
    public string EngineHost => engine.Host;
    public bool CanUndo { get; private set; }
    public bool CanRedo { get; private set; }
    private bool disposed;
    public event EventHandler? Changed;
    private DocumentSession(OfficeEngine engine, JsonObject snapshot, string? path) { this.engine = engine; Snapshot = snapshot; Path = path; Dirty = path is null; }
    public static async Task<DocumentSession> CreateAsync(OfficeEngine engine, string kind, string title)
    {
        var r = await Task.Run(() => engine.Send(new { protocol = 1, command = "CreateDocument", @params = new { kind, title } }));
        return new(engine, r["snapshot"]!.AsObject(), null);
    }
    public static async Task<DocumentSession> OpenAsync(OfficeEngine engine, string path)
    {
        var info = new FileInfo(path);
        if (info.Length > LimitFor(path)) throw new IOException($"Le fichier dépasse la limite de {LimitFor(path) / 1024 / 1024} Mio.");
        CheckFormat(path);
        var bytes = await File.ReadAllBytesAsync(path);
        return await OpenAsync(engine, System.IO.Path.GetFileName(path), bytes, System.IO.Path.GetFullPath(path));
    }
    /// <summary>
    /// Opens a document from its bytes. Used by the browser (files picked or dropped by the user never
    /// have a local path) and by the desktop path overload above. <paramref name="path"/> stays null in the browser.
    /// </summary>
    public static async Task<DocumentSession> OpenAsync(OfficeEngine engine, string fileName, byte[] content, string? path = null)
    {
        var extension = CheckFormat(fileName);
        if (content.LongLength > LimitFor(fileName)) throw new IOException($"Le fichier dépasse la limite de {LimitFor(fileName) / 1024 / 1024} Mio.");
        JsonObject r;
        if (extension is ".oswriter" or ".ossheet" or ".osslides")
        {
            string json;
            try { json = new System.Text.UTF8Encoding(false, true).GetString(content); }
            catch (System.Text.DecoderFallbackException) { throw new InvalidDataException("Le fichier n’est pas un document Nexolume valide (UTF-8 invalide)."); }
            r = await Task.Run(() => engine.Send(new { protocol = 1, command = "OpenDocument", @params = new { json } }));
        }
        else
        {
            var title = System.IO.Path.GetFileNameWithoutExtension(fileName);
            r = await Task.Run(() => engine.ImportOffice(extension[1..], title, content));
        }
        return new(engine, r["snapshot"]!.AsObject(), path) { fileName = System.IO.Path.GetFileName(fileName), Dirty = false };
    }
    public static readonly string[] NativeExtensions = [".oswriter", ".ossheet", ".osslides"];
    public static readonly string[] OfficeExtensions = [".docx", ".odt", ".xlsx", ".pptx"];
    private static long LimitFor(string name) => System.IO.Path.GetExtension(name).ToLowerInvariant() is ".docx" or ".odt" or ".xlsx" or ".pptx" ? 10L * 1024 * 1024 : 16L * 1024 * 1024;
    private static string CheckFormat(string name)
    {
        var extension = System.IO.Path.GetExtension(name).ToLowerInvariant();
        if (!NativeExtensions.Contains(extension) && !OfficeExtensions.Contains(extension)) throw new NotSupportedException($"Le format {extension} n’est pas encore pris en charge.");
        return extension;
    }
    private string? fileName;
    /// <summary>File name shown to the user: from the path on desktop, from the picked/downloaded file in the browser.</summary>
    public string? FileName => Path is not null ? System.IO.Path.GetFileName(Path) : fileName;
    public async Task ExecuteAsync(string command, object? parameters = null)
    {
        await gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            JsonObject reply;
            try { reply = await Task.Run(() => engine.Send(new { protocol = 1, command, document = Id, expected_revision = Revision, @params = parameters ?? new { } })); }
            catch (OfficeException ex) when (ex.Code == "RevisionMismatch") { await ResyncAsync(); throw; }
            try { DocumentProjection.Apply(Snapshot, reply); }
            catch { await ResyncAsync(); throw; }
            Dirty = true; CanUndo = reply["canUndo"]?.GetValue<bool>() ?? false; CanRedo = reply["canRedo"]?.GetValue<bool>() ?? false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally { gate.Release(); }
    }
    private async Task ResyncAsync() { var r = await Task.Run(() => engine.Send(new { protocol = 1, command = "Snapshot", document = Id })); Snapshot = r["snapshot"]!.AsObject(); Dirty = true; CanUndo = r["canUndo"]?.GetValue<bool>() ?? false; CanRedo = r["canRedo"]?.GetValue<bool>() ?? false; Changed?.Invoke(this, EventArgs.Empty); }
    /// <summary>Serializes the current revision in the format named by <paramref name="fileName"/>'s extension.</summary>
    public async Task<byte[]> SerializeAsync(string fileName)
    {
        await gate.WaitAsync();
        try { return await SerializeLockedAsync(fileName); }
        finally { gate.Release(); }
    }
    private async Task<byte[]> SerializeLockedAsync(string fileName)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var extension = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is ".docx" or ".odt" or ".xlsx" or ".pptx")
        {
            var expected = extension switch { ".xlsx" => "sheets", ".pptx" => "slides", _ => "writer" };
            if (Kind != expected) throw new NotSupportedException(extension switch
            {
                ".xlsx" => "Le format XLSX est réservé aux classeurs.",
                ".pptx" => "Le format PPTX est réservé aux présentations.",
                _ => "Les formats DOCX et ODT sont réservés aux documents texte.",
            });
            return await Task.Run(() => engine.ExportOffice(Id, Revision, extension[1..]));
        }
        if (extension is ".oswriter" or ".ossheet" or ".osslides")
        {
            var r = await Task.Run(() => engine.Send(new { protocol = 1, command = "SaveDocument", document = Id, expected_revision = Revision }));
            return System.Text.Encoding.UTF8.GetBytes(r["json"]!.GetValue<string>());
        }
        throw new NotSupportedException($"Le format {extension} n’est pas encore pris en charge.");
    }
    /// <summary>Records that the bytes of revision <paramref name="revision"/> were stored outside the file system (browser download or file handle).</summary>
    public void MarkSaved(string fileName, long revision)
    {
        this.fileName = System.IO.Path.GetFileName(fileName);
        if (revision == Revision) Dirty = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public async Task SaveAsync(string? path = null)
    {
        await gate.WaitAsync();
        string? temporary = null;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var target = System.IO.Path.GetFullPath(path ?? Path ?? throw new InvalidOperationException("Missing save path"));
            var bytes = await SerializeLockedAsync(target);
            temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes); await stream.FlushAsync(); stream.Flush(true);
            }
            if (File.Exists(target)) File.Replace(temporary, target, target + ".bak", true); else File.Move(temporary, target);
            temporary = null; Path = target; Dirty = false; Changed?.Invoke(this, EventArgs.Empty);
        }
        finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); gate.Release(); }
    }
    public void Dispose() { if (disposed) return; disposed = true; engine.Send(new { protocol = 1, command = "CloseDocument", document = Id }); }
}
