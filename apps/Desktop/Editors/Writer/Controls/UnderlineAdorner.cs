using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.VisualTree;
namespace Nexolume.Desktop.Editors.Writer.Controls;
// TextBox handles caret, selection and IME. This overlay adds paragraph underlines
// from its actual shaped and wrapped text, without replacing text input behavior.
public sealed class UnderlineAdorner : Control
{
    private readonly TextBox box;
    public UnderlineAdorner(TextBox box) { this.box = box; IsHitTestVisible = false; box.TextChanged += (_, _) => InvalidateVisual(); box.PropertyChanged += (_, _) => InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        var presenter = box.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        if (presenter is null || string.IsNullOrEmpty(box.Text)) return;
        var origin = presenter.TranslatePoint(default, this) ?? default;
        foreach (var rect in presenter.TextLayout.HitTestTextRange(0, box.Text.Length))
        {
            var y = origin.Y + rect.Bottom - 3;
            context.DrawLine(new Pen(box.Foreground ?? Brushes.Black, 1), new Point(origin.X + rect.Left, y), new Point(origin.X + rect.Right, y));
        }
    }
}
