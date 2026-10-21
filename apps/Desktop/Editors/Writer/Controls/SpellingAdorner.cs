using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.VisualTree;
using Nexolume.Desktop.Editors.Writer.Spelling;

namespace Nexolume.Desktop.Editors.Writer.Controls;

public sealed class SpellingAdorner : Control
{
    private readonly TextBox box;
    public IReadOnlyList<SpellingIssue> Issues { get; private set; } = [];
    public SpellingAdorner(TextBox box)
    {
        this.box = box; IsHitTestVisible = false;
        box.TextChanged += (_, _) => SetIssues([]);
        box.PropertyChanged += (_, _) => InvalidateVisual();
    }
    public void SetIssues(IReadOnlyList<SpellingIssue> issues) { Issues = issues; InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        var presenter = box.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        if (presenter is null || string.IsNullOrEmpty(box.Text)) return;
        var origin = presenter.TranslatePoint(default, this) ?? default; var pen = new Pen(Brush.Parse("#C62828"), 1.2);
        foreach (var issue in Issues)
        {
            if (issue.Start < 0 || issue.Start + issue.Length > box.Text.Length) continue;
            foreach (var rect in presenter.TextLayout.HitTestTextRange(issue.Start, issue.Length))
            {
                var y = origin.Y + rect.Bottom - 1;
                for (var x = origin.X + rect.Left; x < origin.X + rect.Right - 1; x += 4)
                {
                    var right = Math.Min(x + 4, origin.X + rect.Right);
                    context.DrawLine(pen, new Point(x, y), new Point(Math.Min(x + 2, right), y - 2));
                    context.DrawLine(pen, new Point(Math.Min(x + 2, right), y - 2), new Point(right, y));
                }
            }
        }
    }
}
