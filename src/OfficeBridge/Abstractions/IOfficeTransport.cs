using System.Text.Json.Nodes;
namespace OfficeBridge.Abstractions;

/// <summary>
/// Carries one protocol request to a Rust engine instance and returns its raw reply.
/// Implementations: <see cref="Native.NativeOfficeTransport"/> (office_ffi through P/Invoke) and
/// <see cref="Browser.BrowserOfficeTransport"/> (office-wasm through JavaScript interop).
/// Transports never interpret <c>ok</c>/<c>error</c>; <see cref="Services.OfficeEngine"/> does.
/// </summary>
public interface IOfficeTransport : IDisposable
{
    /// <summary>Short host name used in diagnostics ("native", "browser").</summary>
    string Name { get; }
    /// <summary>Executes a JSON request synchronously. Throws <see cref="Models.OfficeTransportException"/> on host failures.</summary>
    JsonObject Execute(string request);
}
