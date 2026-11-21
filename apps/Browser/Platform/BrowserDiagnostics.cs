using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
namespace Nexolume.Desktop.Platform;

/// <summary>
/// Read-only state of the open documents, exported to JavaScript so browser tests (Playwright) can
/// check what the canvas UI did. It never edits documents and sends nothing anywhere.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class BrowserDiagnostics
{
    [JSExport]
    public static string State()
    {
        var view = (Application.Current?.ApplicationLifetime as ISingleViewApplicationLifetime)?.MainView as Shell.MainView;
        if (view is null) return "{\"ready\":false}";
        var state = Dispatcher.UIThread.CheckAccess() ? view.DescribeState() : Dispatcher.UIThread.Invoke(view.DescribeState);
        state["ready"] = true;
        return state.ToJsonString();
    }
}
