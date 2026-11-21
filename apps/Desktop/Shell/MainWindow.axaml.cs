using Avalonia.Controls;
using Avalonia.Markup.Xaml;
namespace Nexolume.Desktop.Shell;

/// <summary>Windows host of the shared <see cref="MainView"/>: owns the native window and its close guard.</summary>
public partial class MainWindow : Window
{
    private bool approvedClose;
    private bool closing;
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this); View = this.FindControl<MainView>("ViewHost")!;
        Closing += async (_, e) =>
        {
            if (approvedClose) return; e.Cancel = true; if (closing) return; closing = true;
            try { if (!await View.CloseAllAsync()) return; approvedClose = true; Close(); }
            catch (Exception ex) { View.ShowError(ex); }
            finally { closing = false; }
        };
        Closed += (_, _) => View.Dispose();
    }
    public MainView View { get; }
    public Task CreateAsync(string kind) => View.CreateAsync(kind);
    public Task OpenAsync(string path) => View.OpenAsync(path);
    public Task<bool> CloseTabAsync(TabItem tab) => View.CloseTabAsync(tab);
    internal Task SmokeAsync(string output) => View.SmokeAsync(output);
}
