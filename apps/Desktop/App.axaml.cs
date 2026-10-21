using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Controls.ApplicationLifetimes;
namespace Nexolume.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new Shell.MainWindow(); desktop.MainWindow = window;
            if (Services.BatchOutput.Handles(desktop.Args ?? []))
            {
                window.ShowInTaskbar = false; window.ShowActivated = false; window.WindowState = Avalonia.Controls.WindowState.Minimized;
                window.Opened += async (_, _) => { window.Hide(); try { await Services.BatchOutput.RunAsync(desktop.Args!); desktop.Shutdown(0); } catch (Exception ex) { Services.AppLog.Write(ex); desktop.Shutdown(1); } };
            }
            var smoke = desktop.Args?.FirstOrDefault(a => a.StartsWith("--smoke-test="));
            if (smoke is not null)
            {
                window.ShowInTaskbar = false; window.ShowActivated = false; window.WindowState = Avalonia.Controls.WindowState.Minimized;
                window.Opened += async (_, _) => { window.Hide(); var output = System.IO.Path.GetFullPath(smoke[13..]); try { await window.SmokeAsync(output); desktop.Shutdown(0); } catch (Exception ex) { Services.AppLog.Write(ex); System.IO.File.WriteAllText(output, ex.ToString()); desktop.Shutdown(1); } };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
