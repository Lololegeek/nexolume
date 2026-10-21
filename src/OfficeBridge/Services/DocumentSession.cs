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
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var info = new FileInfo(path);
        var limit = extension is ".docx" or ".odt" ? 10L * 1024 * 1024 : 16L * 1024 * 1024;
        if (info.Length > limit) throw new IOException($"Le fichier dépasse la limite de {limit / 1024 / 1024} Mio.");
        JsonObject r;
        if (extension is ".oswriter" or ".ossheet" or ".osslides")
        {
            var json = await File.ReadAllTextAsync(path);
            r = await Task.Run(() => engine.Send(new { protocol = 1, command = "OpenDocument", @params = new { json } }));
        }
        else if (extension is ".docx" or ".odt")
        {
            var bytes = await File.ReadAllBytesAsync(path);
            var title = System.IO.Path.GetFileNameWithoutExtension(path);
            r = await Task.Run(() => engine.ImportOffice(extension[1..], title, bytes));
        }
        else throw new NotSupportedException($"Le format {extension} n’est pas encore pris en charge.");
        return new(engine, r["snapshot"]!.AsObject(), System.IO.Path.GetFullPath(path));
    }
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
    public async Task SaveAsync(string? path = null)
    {
        await gate.WaitAsync();
        string? temporary = null;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var target = System.IO.Path.GetFullPath(path ?? Path ?? throw new InvalidOperationException("Missing save path"));
            var extension = System.IO.Path.GetExtension(target).ToLowerInvariant();
            byte[] bytes;
            if (extension is ".docx" or ".odt")
            {
                if (Kind != "writer") throw new NotSupportedException("Les formats DOCX et ODT sont réservés aux documents texte.");
                bytes = await Task.Run(() => engine.ExportOffice(Id, Revision, extension[1..]));
            }
            else if (extension is ".oswriter" or ".ossheet" or ".osslides")
            {
                var r = await Task.Run(() => engine.Send(new { protocol = 1, command = "SaveDocument", document = Id, expected_revision = Revision }));
                bytes = System.Text.Encoding.UTF8.GetBytes(r["json"]!.GetValue<string>());
            }
            else throw new NotSupportedException($"Le format {extension} n’est pas encore pris en charge.");
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
