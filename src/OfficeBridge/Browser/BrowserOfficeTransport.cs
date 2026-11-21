using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using OfficeBridge.Abstractions;
using OfficeBridge.Models;
namespace OfficeBridge.Browser;

/// <summary>
/// Browser transport: the Rust engine compiled to WebAssembly (crate office-wasm) and loaded by
/// <c>nexolume-engine.js</c>. Calls are synchronous: .NET and the engine share the browser main
/// thread, and each request/reply string crosses the boundary once (wasm-bindgen encodes UTF-8).
/// </summary>
[SupportedOSPlatform("browser")]
public sealed partial class BrowserOfficeTransport : IOfficeTransport
{
    public const string ModuleName = "nexolume-engine";
    private static bool loaded;
    private int handle;

    public BrowserOfficeTransport()
    {
        if (!loaded) throw new OfficeTransportException("EngineNotLoaded", "Le moteur WebAssembly n’est pas chargé.");
        handle = Wrap(Create);
        if (handle <= 0) throw new OfficeTransportException("EngineUnavailable", "Impossible de créer une instance du moteur WebAssembly.");
    }
    public string Name => "browser";
    public static bool IsLoaded => loaded;

    /// <summary>Imports the JavaScript loader and instantiates office_wasm_bg.wasm.</summary>
    public static async Task LoadAsync(string moduleUrl)
    {
        if (loaded) return;
        await JSHost.ImportAsync(ModuleName, moduleUrl);
        await Initialize();
        loaded = true;
    }

    public JsonObject Execute(string request)
    {
        ObjectDisposedException.ThrowIf(handle == 0, this);
        var reply = Wrap(() => Run(handle, request));
        return JsonNode.Parse(reply)?.AsObject() ?? throw new OfficeTransportException("InvalidReply", "Réponse vide du moteur WebAssembly.");
    }

    public void Dispose()
    {
        if (handle == 0) return;
        var current = handle; handle = 0;
        try { Destroy(current); } catch (JSException) { /* A crashed instance has nothing left to release. */ }
    }

    private static T Wrap<T>(Func<T> call)
    {
        try { return call(); }
        catch (JSException ex)
        {
            // Messages are "Code: detail" (office-wasm HostError) or "EngineCrashed: ..." from the loader.
            var message = ex.Message.StartsWith("Error: ", StringComparison.Ordinal) ? ex.Message[7..] : ex.Message;
            var separator = message.IndexOf(':');
            var code = separator > 0 && message[..separator].All(char.IsAsciiLetter) ? message[..separator] : "EngineError";
            throw new OfficeTransportException(code, message, ex);
        }
    }

    [JSImport("initialize", ModuleName)] private static partial Task Initialize();
    [JSImport("create", ModuleName)] private static partial int Create();
    [JSImport("destroy", ModuleName)] private static partial bool Destroy(int handle);
    [JSImport("execute", ModuleName)] private static partial string Run(int handle, string request);
}
