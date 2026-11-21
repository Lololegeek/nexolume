using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Nexolume.Desktop.Platform;

/// <summary>
/// Browser counterpart of the desktop DialogWindow: a page has a single view, so dialogs are shown
/// as modal cards in the shell's dialog layer. It offers the Window members the shared dialogs use
/// (Title, sizing, Opened/Closing/Closed, Close(result)), so the dialog code is identical on both heads.
/// </summary>
public class DialogWindow : ContentControl
{
    private TaskCompletionSource<object?>? completion;
    private Control? host;
    private IInputElement? previousFocus;

    protected override Type StyleKeyOverride => typeof(ContentControl);
    public string? Title { get; set; }
    public bool CanResize { get; set; } = true;
    public bool ShowInTaskbar { get; set; } = true;
    public WindowStartupLocation WindowStartupLocation { get; set; }
    public SizeToContent SizeToContent { get; set; }
    public WindowState WindowState { get; set; }
    public bool IsOpen => host is not null;
    public IStorageProvider StorageProvider => TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("Dialogue fermé.");

    public event EventHandler? Opened;
    public event EventHandler<CancelEventArgs>? Closing;
    public event EventHandler? Closed;

    public async Task<T?> ShowModalAsync<T>(Visual anchor) => await Open() is T value ? value : default;
    public Task ShowModalAsync(Visual anchor) => Open();
    public void ShowOver(Visual anchor) => _ = Open();

    public void Close() => Close(null);
    public void Close(object? result)
    {
        if (host is null) return;
        var closing = new CancelEventArgs(); Closing?.Invoke(this, closing); if (closing.Cancel) return;
        if (WindowState == WindowState.FullScreen) AppPlatform.Current.SetFullScreen(this, false);
        var layer = (Panel)host.Parent!; layer.Children.Remove(host); layer.IsVisible = layer.Children.Count > 0;
        if (host is Grid grid) grid.Children.Clear(); else if (host is Border border) border.Child = null;
        host = null; Closed?.Invoke(this, EventArgs.Empty);
        previousFocus?.Focus(); previousFocus = null;
        var done = completion; completion = null; done?.TrySetResult(result);
    }

    private static Panel Layer() =>
        (Application.Current?.ApplicationLifetime as ISingleViewApplicationLifetime)?.MainView is Shell.MainView view
            ? view.DialogLayer : throw new InvalidOperationException("Aucune page pour afficher ce dialogue.");

    private Task<object?> Open()
    {
        if (completion is not null) return completion.Task;
        var layer = Layer(); completion = new TaskCompletionSource<object?>();
        previousFocus = TopLevel.GetTopLevel(layer)?.FocusManager?.GetFocusedElement();
        if (WindowState == WindowState.FullScreen)
        {
            // Slideshow: the whole page, plus the browser's own full screen when the user allows it.
            host = new Border { Background = Brushes.Black, Child = this };
            AppPlatform.Current.SetFullScreen(this, true);
        }
        else
        {
            var available = layer.Bounds.Size;
            if (available.Width > 0 && !double.IsNaN(Width) && Width > available.Width - 24) Width = Math.Max(MinWidth, available.Width - 24);
            if (available.Height > 0 && !double.IsNaN(Height) && Height > available.Height - 72) Height = Math.Max(Math.Min(MinHeight, available.Height - 72), available.Height - 72);
            var title = new TextBlock { Text = Title ?? "", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var close = new Button { Content = "×", Padding = new Thickness(10, 0), MinHeight = 28, [ToolTip.TipProperty] = "Fermer (Échap)" };
            close.Classes.Add("quiet"); close.Click += (_, _) => Close();
            var bar = new DockPanel { Margin = new Thickness(16, 8, 8, 4) }; DockPanel.SetDock(close, Dock.Right); bar.Children.Add(close); bar.Children.Add(title);
            var body = new DockPanel(); DockPanel.SetDock(bar, Dock.Top); body.Children.Add(bar); body.Children.Add(this);
            var card = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, BoxShadow = BoxShadows.Parse("0 12 40 0 #66000000"), Child = body };
            card.Bind(Border.BackgroundProperty, card.GetResourceObservable("OfficeBackground"));
            card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("OfficeLine"));
            // The backdrop catches pointer input so the page behind behaves as with a modal window.
            var backdrop = new Grid { Background = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)) };
            backdrop.Children.Add(card); host = backdrop;
        }
        host.AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Escape && !e.Handled) { e.Handled = true; Close(); } });
        layer.Children.Add(host); layer.IsVisible = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (host is null) return;
            Opened?.Invoke(this, EventArgs.Empty);
            if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Visual focused || !this.IsVisualAncestorOf(focused))
                (KeyboardNavigationHandler.GetNext(this, NavigationDirection.Next) ?? this).Focus();
        }, DispatcherPriority.Loaded);
        return completion.Task;
    }
}
