using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
namespace Nexolume.Desktop.Platform;

/// <summary>Imports of wwwroot/interop.js (module "nexolume-interop"). Byte views are copied synchronously by the script.</summary>
[SupportedOSPlatform("browser")]
internal static partial class BrowserInterop
{
    public const string ModuleName = "nexolume-interop";

    [JSImport("readSetting", ModuleName)] public static partial string? ReadSetting(string key);
    [JSImport("writeSetting", ModuleName)] public static partial bool WriteSetting(string key, string value);
    [JSImport("openUrl", ModuleName)] public static partial void OpenUrl(string url);
    [JSImport("setUnsavedChanges", ModuleName)] public static partial void SetUnsavedChanges(bool unsaved);
    [JSImport("hasSavePicker", ModuleName)] public static partial bool HasSavePicker();
    [JSImport("download", ModuleName)] public static partial void Download(string fileName, string mime, [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> bytes);
    [JSImport("printPdf", ModuleName)] public static partial void PrintPdf(string title, [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> bytes);
    [JSImport("setFullScreen", ModuleName)] public static partial void SetFullScreen(bool fullScreen);
    [JSImport("logError", ModuleName)] public static partial void LogError(string message);
    [JSImport("localFontsSupported", ModuleName)] public static partial bool LocalFontsSupported();
    [JSImport("localFontsPermission", ModuleName)] public static partial Task<string> LocalFontsPermission();
    [JSImport("listLocalFonts", ModuleName)] public static partial Task<string> ListLocalFonts();
    [JSImport("loadLocalFontFamily", ModuleName)] public static partial Task<string> LoadLocalFontFamily(string family);
}
