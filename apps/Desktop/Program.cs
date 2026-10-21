using Avalonia;
namespace Nexolume.Desktop;

internal static class Program
{
    [STAThread] public static void Main(string[] args) { try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); } catch (Exception ex) { Services.AppLog.Write(ex); throw; } }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
