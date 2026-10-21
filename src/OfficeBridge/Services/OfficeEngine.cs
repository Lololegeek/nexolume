using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using OfficeBridge.Native;
namespace OfficeBridge.Services;

public sealed class OfficeEngine : IDisposable
{
    private readonly EngineHandle handle = new();
    public JsonObject Send(object request)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        var bytes = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(request));
        var held = false;
        try
        {
            handle.DangerousAddRef(ref held);
            var code = NativeMethods.office_execute((ulong)handle.DangerousGetHandle(), bytes, (nuint)bytes.Length, out var output);
            if (code != 0) throw new InvalidOperationException($"Native engine error: {code}");
            try { var data = new byte[checked((int)output.Length)]; Marshal.Copy(output.Pointer, data, 0, data.Length); var result = JsonNode.Parse(data)!.AsObject(); if (result["ok"]?.GetValue<bool>() != true) throw new OfficeBridge.Models.OfficeException(result["error"]?["code"]?.GetValue<string>() ?? "NativeError", result["error"]?["message"]?.GetValue<string>() ?? "Unknown engine error"); return result; }
            finally { NativeMethods.office_free(output.Pointer); }
        }
        finally { if (held) handle.DangerousRelease(); }
    }
    public JsonObject ImportOffice(string format, string title, byte[] content)
    {
        if (content.Length > 10 * 1024 * 1024) throw new IOException("Le fichier Office dépasse la limite d’importation de 10 Mio.");
        var encoded = Convert.ToBase64String(content);
        return Send(new { protocol = 1, command = "ImportOfficeDocument", @params = new { format, title, data = encoded } });
    }
    public byte[] ExportOffice(string document, long revision, string format)
    {
        var result = Send(new { protocol = 1, command = "ExportOfficeDocument", document, expected_revision = revision, @params = new { format } });
        var encoded = result["data"]?.GetValue<string>() ?? throw new InvalidDataException("Le moteur n’a renvoyé aucun fichier Office.");
        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length > 10 * 1024 * 1024) throw new IOException("Le fichier exporté dépasse la limite de 10 Mio.");
        return bytes;
    }
    public void Dispose() => handle.Dispose();
}
