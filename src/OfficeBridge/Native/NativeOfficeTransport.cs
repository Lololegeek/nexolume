using System.Text;
using System.Text.Json.Nodes;
using OfficeBridge.Abstractions;
using OfficeBridge.Models;
namespace OfficeBridge.Native;

/// <summary>Desktop transport: office_ffi shared library loaded through P/Invoke.</summary>
public sealed class NativeOfficeTransport : IOfficeTransport
{
    private readonly EngineHandle handle = new();
    public string Name => "native";
    public JsonObject Execute(string request)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        var bytes = Encoding.UTF8.GetBytes(request);
        var held = false;
        try
        {
            handle.DangerousAddRef(ref held);
            var code = NativeMethods.office_execute((ulong)handle.DangerousGetHandle(), bytes, (nuint)bytes.Length, out var output);
            if (code != 0) throw new OfficeTransportException(code switch { 1 => "InvalidArguments", 3 => "EnginePanic", _ => "InvalidRequest" }, $"Native engine error: {code}");
            try
            {
                // Parse straight from native memory: no intermediate managed copy of the reply.
                unsafe { return JsonNode.Parse(new ReadOnlySpan<byte>((void*)output.Pointer, checked((int)output.Length)))!.AsObject(); }
            }
            finally { NativeMethods.office_free(output.Pointer); }
        }
        finally { if (held) handle.DangerousRelease(); }
    }
    public void Dispose() => handle.Dispose();
}
