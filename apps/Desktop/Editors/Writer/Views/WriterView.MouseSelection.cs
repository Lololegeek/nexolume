using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Nexolume.Desktop.Editors.Writer.Views;

public partial class WriterView
{
    private readonly DispatcherTimer mouseSelectionTimer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private IPointer? selectionPointer;
    private Point mouseInViewport;

    private void SetupMouseSelection()
    {
        var panel = this.FindControl<StackPanel>("ParagraphPanel")!;
        var page = this.FindControl<Border>("EditorPage")!;
        var scroll = this.FindControl<ScrollViewer>("PageScroll")!;
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Source is not Visual source || !page.IsVisualAncestorOf(source) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            // Table cells and pictures retain their own pointer interactions.
            var sourceBox = source as TextBox ?? source.FindAncestorOfType<TextBox>();
            if (sourceBox is not null && sourceBox is not Controls.RichParagraphBox || source.FindAncestorOfType<Border>() is { } border && imageViews.Values.Contains(border)) return;
            var hit = HitParagraph(e.GetPosition(panel)); if (hit is not { } target) return;
            if (e.ClickCount > 1)
            {
                StopMouseSelection();
                if (multi is not null) ExitMulti(target.Id, target.Offset, target.Offset);
                return; // Native word/paragraph selection on double/triple click.
            }
            var anchor = e.KeyModifiers.HasFlag(KeyModifiers.Shift) && active is not null && boxes.TryGetValue(active, out var current)
                ? multi is { } m ? (m.Anchor, m.AnchorOffset) : (active, current.SelectionStart != current.SelectionEnd ? current.SelectionStart : current.CaretIndex)
                : (target.Id, target.Offset);
            pressId = anchor.Item1; pressOffset = anchor.Item2; dragging = true;
            selectionPointer = e.Pointer; e.Pointer.Capture(panel);
            mouseInViewport = e.GetPosition(scroll);
            BeginMulti(pressId, pressOffset, target.Id, target.Offset);
            mouseSelectionTimer.Start(); e.Handled = true;
        }, RoutingStrategies.Tunnel, true);
        AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (!dragging || selectionPointer != e.Pointer) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { StopMouseSelection(); return; }
            mouseInViewport = e.GetPosition(scroll); ExtendMouseSelection(e.GetPosition(panel)); e.Handled = true;
        }, RoutingStrategies.Tunnel, true);
        AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (!dragging || selectionPointer != e.Pointer) return;
            ExtendMouseSelection(e.GetPosition(panel)); StopMouseSelection(); e.Handled = true;
        }, RoutingStrategies.Tunnel, true);
        panel.PointerCaptureLost += (_, e) => { if (e.Source == panel && e.Pointer.Captured != panel) StopMouseSelection(); };
        mouseSelectionTimer.Tick += (_, _) =>
        {
            if (!dragging) return;
            const double edge = 32;
            var step = mouseInViewport.Y < edge ? -Math.Clamp(edge - mouseInViewport.Y, 4, 48) : mouseInViewport.Y > scroll.Bounds.Height - edge ? Math.Clamp(mouseInViewport.Y - scroll.Bounds.Height + edge, 4, 48) : 0;
            if (step == 0) return;
            var next = Math.Clamp(scroll.Offset.Y + step, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
            if (Math.Abs(next - scroll.Offset.Y) < .01) return;
            scroll.Offset = new Vector(scroll.Offset.X, next); scroll.UpdateLayout();
            if (scroll.TranslatePoint(mouseInViewport, panel) is { } position) ExtendMouseSelection(position);
        };
    }

    private (string Id, int Offset)? HitParagraph(Point position)
    {
        var panel = this.FindControl<StackPanel>("ParagraphPanel")!;
        (string Id, Controls.RichParagraphBox Box, Point Origin)? nearest = null; var distance = double.PositiveInfinity;
        foreach (var p in Paras)
        {
            var id = p!["id"]!.GetValue<string>();
            if (!IsText(p) || !boxes.TryGetValue(id, out var box) || box.TranslatePoint(default, panel) is not { } origin) continue;
            var gap = position.Y < origin.Y ? origin.Y - position.Y : Math.Max(0, position.Y - origin.Y - box.Bounds.Height);
            if (gap >= distance) continue;
            distance = gap; nearest = (id, box, origin);
        }
        if (nearest is not { } target) return null;
        var text = target.Box.Text ?? "";
        if (position.Y < target.Origin.Y) return (target.Id, 0);
        if (position.Y > target.Origin.Y + target.Box.Bounds.Height) return (target.Id, text.Length);
        var presenter = PresenterOf(target.Box);
        var offset = presenter?.TextLayout is { } layout && panel.TranslatePoint(position, presenter) is { } point ? layout.HitTestPoint(point).TextPosition : 0;
        offset = Math.Clamp(offset, 0, text.Length);
        if (offset > 0 && offset < text.Length && char.IsLowSurrogate(text[offset])) offset--;
        return (target.Id, offset);
    }

    private void ExtendMouseSelection(Point position)
    {
        if (pressId is null || HitParagraph(position) is not { } hit) return;
        BeginMulti(pressId, Math.Clamp(pressOffset, 0, TextOf(pressId).Length), hit.Id, hit.Offset);
    }
    private void StopMouseSelection()
    {
        dragging = false; mouseSelectionTimer.Stop();
        var pointer = selectionPointer; selectionPointer = null;
        if (pointer is not null && pointer.Captured == this.FindControl<StackPanel>("ParagraphPanel")) pointer.Capture(null);
    }
}
