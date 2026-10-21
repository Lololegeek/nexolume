using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Nexolume.Desktop.Editors.Slides.Controls;
using System.Text.Json.Nodes;
namespace Nexolume.Desktop.Editors.Slides.Views;

public sealed class SlideshowWindow : Window
{
    private readonly JsonArray slides;
    private readonly SlideSurface surface = new() { IsHitTestVisible = false };
    private readonly TextBlock label = new() { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock notes = new() { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Center, IsVisible = false, Margin = new Thickness(16, 0) };
    private readonly TranslateTransform shift = new();
    private Avalonia.Threading.DispatcherTimer? animation;
    public int SlideIndex { get; private set; }
    public bool NotesVisible => notes.IsVisible;
    public SlideshowWindow(JsonArray source, int start = 0)
    {
        Title = "Nexolume · Présentation"; Focusable = true; Opened += (_, _) => Focus(); slides = (JsonArray)source.DeepClone(); SlideIndex = Math.Clamp(start, 0, slides.Count - 1); WindowState = WindowState.FullScreen; Background = Brushes.Black;
        surface.RenderTransform = shift; var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto") }; grid.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = surface, Margin = new Thickness(0, 12) }); var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 18, Margin = new Thickness(16) };
        void Button(string text, Action action) { var button = new Button { Content = text }; button.Click += (_, _) => action(); bar.Children.Add(button); }
        Button("← Précédente", () => Navigate(-1)); bar.Children.Add(label); Button("Suivante →", () => Navigate(1)); Button("Notes · N", ToggleNotes); Button("Quitter · Échap", Close); Grid.SetRow(notes, 1); grid.Children.Add(notes); Grid.SetRow(bar, 2); grid.Children.Add(bar); Content = grid;
        AddHandler(KeyDownEvent, (_, e) => { switch (e.Key) { case Key.Escape: Close(); break; case Key.N: ToggleNotes(); break; case Key.Right: case Key.Space: case Key.PageDown: case Key.Enter: Navigate(1); break; case Key.Left: case Key.PageUp: Navigate(-1); break; case Key.Home: SlideIndex = 0; Render(); break; case Key.End: SlideIndex = slides.Count - 1; Render(); break; default: return; } e.Handled = true; }, Avalonia.Interactivity.RoutingStrategies.Tunnel); Render();
    }
    public void ToggleNotes() { notes.IsVisible = !notes.IsVisible; }
    public void Navigate(int delta)
    {
        var next = Math.Clamp(SlideIndex + delta, 0, slides.Count - 1); var moved = next != SlideIndex; SlideIndex = next; Render();
        if (moved) Animate(slides[SlideIndex]!["transition"]?.GetValue<string>() ?? "none", delta);
    }
    // Short fade or slide-in; the final state is always the normal one even if a timer tick is missed.
    private void Animate(string transition, int direction)
    {
        animation?.Stop(); surface.Opacity = 1; shift.X = 0;
        if (transition == "none") return;
        var step = 0; animation = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        animation.Tick += (_, _) =>
        {
            step++; var t = Math.Min(1.0, step / 18.0); var eased = 1 - Math.Pow(1 - t, 3);
            if (transition == "fade") surface.Opacity = eased; else { surface.Opacity = 0.4 + 0.6 * eased; shift.X = (direction >= 0 ? 1 : -1) * 240 * (1 - eased); }
            if (t >= 1) { animation!.Stop(); surface.Opacity = 1; shift.X = 0; }
        };
        if (transition == "fade") surface.Opacity = 0; else shift.X = (direction >= 0 ? 1 : -1) * 240;
        animation.Start();
    }
    private void Render() { var slide = slides[SlideIndex]!; surface.Objects = slide["objects"]!.AsArray(); surface.Background = slide["background"]?.GetValue<string>() ?? "#FFFFFF"; notes.Text = slide["notes"]?.GetValue<string>() is { Length: > 0 } text ? text : "Aucune note pour cette diapositive."; surface.InvalidateVisual(); label.Text = $"{SlideIndex + 1} / {slides.Count}"; }
}
