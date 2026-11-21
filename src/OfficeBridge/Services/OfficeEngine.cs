using System.Text.Json.Nodes;
using OfficeBridge.Abstractions;
using OfficeBridge.Browser;
using OfficeBridge.Models;
using OfficeBridge.Native;
namespace OfficeBridge.Services;

/// <summary>
/// Platform-neutral client of the Rust engine. It owns one transport (native DLL on desktop,
/// WebAssembly in the browser) and turns protocol replies into results or <see cref="OfficeException"/>.
/// </summary>
public sealed class OfficeEngine : IDisposable
{
    public const int OfficeFileLimit = 10 * 1024 * 1024;
    private readonly IOfficeTransport transport;
    private bool disposed;

    /// <summary>Creates an engine on the transport matching the current platform.</summary>
    public OfficeEngine() : this(CreateDefaultTransport()) { }
    public OfficeEngine(IOfficeTransport transport) => this.transport = transport;
    public string Host => transport.Name;

    public static IOfficeTransport CreateDefaultTransport() =>
        OperatingSystem.IsBrowser() ? new BrowserOfficeTransport() : new NativeOfficeTransport();

    public JsonObject Send(object request)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var result = transport.Execute(System.Text.Json.JsonSerializer.Serialize(request));
        if (result["ok"]?.GetValue<bool>() != true)
            throw new OfficeException(result["error"]?["code"]?.GetValue<string>() ?? "EngineError", result["error"]?["message"]?.GetValue<string>() ?? "Unknown engine error");
        return result;
    }
    public JsonObject ImportOffice(string format, string title, byte[] content)
    {
        if (content.Length > OfficeFileLimit) throw new IOException("Le fichier Office dépasse la limite d’importation de 10 Mio.");
        var encoded = Convert.ToBase64String(content);
        return Send(new { protocol = 1, command = "ImportOfficeDocument", @params = new { format, title, data = encoded } });
    }
    public byte[] ExportOffice(string document, long revision, string format)
    {
        var result = Send(new { protocol = 1, command = "ExportOfficeDocument", document, expected_revision = revision, @params = new { format } });
        var encoded = result["data"]?.GetValue<string>() ?? throw new InvalidDataException("Le moteur n’a renvoyé aucun fichier Office.");
        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length > OfficeFileLimit) throw new IOException("Le fichier exporté dépasse la limite de 10 Mio.");
        return bytes;
    }
    public void Dispose() { if (disposed) return; disposed = true; transport.Dispose(); }
}
