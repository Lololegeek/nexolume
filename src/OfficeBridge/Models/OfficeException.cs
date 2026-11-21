namespace OfficeBridge.Models;

/// <summary>A command the engine rejected (reply with <c>ok: false</c>).</summary>
public sealed class OfficeException(string code, string message) : Exception(message) { public string Code { get; } = code; }

/// <summary>The engine host itself failed (invalid request, closed engine, crashed WebAssembly instance...).</summary>
public sealed class OfficeTransportException(string code, string message, Exception? inner = null) : InvalidOperationException(message, inner)
{
    public string Code { get; } = code;
}
