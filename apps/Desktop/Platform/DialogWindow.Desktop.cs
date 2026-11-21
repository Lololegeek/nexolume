using Avalonia;
using Avalonia.Controls;

namespace Nexolume.Desktop.Platform;

/// <summary>
/// Desktop dialogs are real owned windows. The browser build compiles another DialogWindow with
/// the same members that shows the content in an overlay of the single page view.
/// </summary>
public class DialogWindow : Window
{
    protected override Type StyleKeyOverride => typeof(Window);
    private static Window? Owner(Visual anchor) => anchor as Window ?? TopLevel.GetTopLevel(anchor) as Window;

    /// <summary>Shows the dialog modally over the window containing <paramref name="anchor"/>.</summary>
    public Task<T?> ShowModalAsync<T>(Visual anchor) =>
        Owner(anchor) is { } owner ? ShowDialog<T?>(owner) : throw new InvalidOperationException("Aucune fenêtre parente pour ce dialogue.");
    public Task ShowModalAsync(Visual anchor) =>
        Owner(anchor) is { } owner ? ShowDialog(owner) : throw new InvalidOperationException("Aucune fenêtre parente pour ce dialogue.");
    /// <summary>Shows the dialog without blocking its owner (slideshow).</summary>
    public void ShowOver(Visual anchor) { if (Owner(anchor) is { } owner) Show(owner); else Show(); }
}
