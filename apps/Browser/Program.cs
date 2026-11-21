using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Browser;
using OfficeBridge.Browser;
namespace Nexolume.Desktop;

[SupportedOSPlatform("browser")]
internal static partial class Program
{
    private static async Task Main()
    {
        try
        {
            // Paths are relative to _framework/ (where dotnet.js lives).
            await JSHost.ImportAsync(Platform.BrowserInterop.ModuleName, "../interop.js");
            await BrowserOfficeTransport.LoadAsync("../engine/nexolume-engine.js");
            await BuildAvaloniaApp().WithInterFont().StartBrowserAppAsync("out");
            Ready();
        }
        catch (Exception ex) { Services.AppLog.Write(ex); Failed(ex.Message); throw; }
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().LogToTrace();

    [JSImport("appReady", Platform.BrowserInterop.ModuleName)] private static partial void Ready();
    [JSImport("appFailed", Platform.BrowserInterop.ModuleName)] private static partial void Failed(string message);
}
