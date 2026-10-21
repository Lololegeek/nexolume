using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using System.Globalization;
using System.Text.Json.Nodes;
namespace Nexolume.Desktop.Editors.Writer.Views;

/// <summary>Comments and tracked changes: both live in undoable document-level lists and are shown in the review pane.</summary>
public partial class WriterView
{
    private readonly Dictionary<string, string> commentDrafts = [];
    private string reviewSignature = "";
    private string? focusComment;
    private bool tracking, reviewOpen, reviewOpenedOnce;
    public bool TrackingChanges => tracking;
    public bool ReviewPaneOpen => reviewOpen;

    private void SetupReview()
    {
        foreach (var name in new[] { "NewCommentButton", "InsertCommentButton" }) this.FindControl<Button>(name)!.Click += async (_, _) => await AddCommentAsync();
        this.FindControl<Button>("ReviewPaneButton")!.Click += (_, _) => { reviewOpen = !reviewOpen; reviewSignature = ""; RenderReview(); };
        this.FindControl<Button>("TrackChangesButton")!.Click += async (_, _) => { await FlushAsync(); tracking = !tracking; if (tracking) reviewOpen = true; reviewSignature = ""; RenderReview(); };
        this.FindControl<Button>("AcceptAllButton")!.Click += async (_, _) => await ResolveRevisionsAsync(null, true);
        this.FindControl<Button>("RejectAllButton")!.Click += async (_, _) => await ResolveRevisionsAsync(null, false);
    }

    private static string Author() => string.IsNullOrWhiteSpace(Environment.UserName) ? "Auteur" : Environment.UserName;
    private Dictionary<string, int> ParagraphOrder() => Document.Snapshot["content"]!["paragraphs"]!.AsArray().Select((p, i) => (Id: p!["id"]!.GetValue<string>(), i)).ToDictionary(x => x.Id, x => x.i);
    private List<JsonObject> LiveList(string key)
    {
        var order = ParagraphOrder();
        return (Document.Snapshot[key] as JsonArray ?? []).OfType<JsonObject>().Where(n => order.ContainsKey(n["paragraph"]!.GetValue<string>())).OrderBy(n => order[n["paragraph"]!.GetValue<string>()]).ToList();
    }
    private async Task SetListAsync(string command, string key, IEnumerable<JsonObject> items)
    {
        var list = new JsonArray(items.Select(n => (JsonNode)n.DeepClone()).ToArray());
        await Enqueue(() => Document.ExecuteAsync(command, key == "comments" ? new { comments = list } : (object)new { revisions = list }));
    }

    public async Task AddCommentAsync(string text = "")
    {
        await FlushAsync(); if (active is null || !boxes.ContainsKey(active)) return;
        var id = Guid.NewGuid().ToString("N")[..12]; focusComment = id; reviewOpen = true;
        var items = LiveList("comments"); items.Add(new JsonObject { ["id"] = id, ["paragraph"] = active, ["author"] = Author(), ["text"] = text, ["date"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture), ["resolved"] = false });
        await SetListAsync("SetComments", "comments", items);
    }

    private async Task FlushCommentsAsync()
    {
        if (commentDrafts.Count == 0) return;
        var pending = commentDrafts.ToArray(); commentDrafts.Clear();
        var items = LiveList("comments"); foreach (var (id, text) in pending) if (items.FirstOrDefault(n => n["id"]!.GetValue<string>() == id) is { } item) item["text"] = text;
        await SetListAsync("SetComments", "comments", items);
    }

    /// <summary>Remembers the previous text of every paragraph about to be overwritten while tracking is on.</summary>
    private async Task TrackEditsAsync(IEnumerable<KeyValuePair<string, string>> edits)
    {
        if (!tracking) return;
        var items = LiveList("revisions"); var paragraphs = Document.Snapshot["content"]!["paragraphs"]!.AsArray(); var changed = false;
        foreach (var (id, text) in edits)
        {
            if (items.Any(r => r["paragraph"]!.GetValue<string>() == id)) continue;
            var before = paragraphs.FirstOrDefault(p => p!["id"]!.GetValue<string>() == id)?["text"]?.GetValue<string>(); if (before is null || before == text) continue;
            items.Add(new JsonObject { ["id"] = Guid.NewGuid().ToString("N")[..12], ["paragraph"] = id, ["author"] = Author(), ["date"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture), ["kind"] = "edit", ["old"] = before }); changed = true;
        }
        if (changed) await SetListAsync("SetRevisions", "revisions", items);
    }

    private async Task TrackInsertAsync(string paragraph)
    {
        if (!tracking) return;
        var items = LiveList("revisions"); if (items.Any(r => r["paragraph"]!.GetValue<string>() == paragraph)) return;
        items.Add(new JsonObject { ["id"] = Guid.NewGuid().ToString("N")[..12], ["paragraph"] = paragraph, ["author"] = Author(), ["date"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture), ["kind"] = "insert", ["old"] = "" });
        await SetListAsync("SetRevisions", "revisions", items);
    }

    /// <summary>Accepts or refuses one revision, or every revision when <paramref name="id"/> is null.</summary>
    public async Task ResolveRevisionsAsync(string? id, bool accept)
    {
        await FlushAsync();
        var items = LiveList("revisions"); var targets = items.Where(r => id is null || r["id"]!.GetValue<string>() == id).ToList(); if (targets.Count == 0) return;
        if (!accept)
        {
            foreach (var revision in targets)
            {
                var paragraph = revision["paragraph"]!.GetValue<string>(); var kind = revision["kind"]!.GetValue<string>();
                if (kind == "insert") { if (Document.Snapshot["content"]!["paragraphs"]!.AsArray().Count > 1) await Enqueue(() => Document.ExecuteAsync("DeleteParagraph", new { id = paragraph })); }
                else { var old = revision["old"]!.GetValue<string>(); await Enqueue(() => Document.ExecuteAsync("SetParagraph", new { id = paragraph, text = old })); }
            }
        }
        var gone = targets.Select(t => t["id"]!.GetValue<string>()).ToHashSet();
        await SetListAsync("SetRevisions", "revisions", LiveList("revisions").Where(r => !gone.Contains(r["id"]!.GetValue<string>())));
    }

    private async Task ChangeCommentAsync(string id, Action<JsonObject> change)
    {
        await FlushCommentsAsync();
        var items = LiveList("comments"); if (items.FirstOrDefault(n => n["id"]!.GetValue<string>() == id) is { } item) change(item);
        await SetListAsync("SetComments", "comments", items);
    }

    private IBrush LineBrush() => this.TryFindResource("OfficeLine", ActualThemeVariant, out var found) && found is IBrush brush ? brush : Brushes.Gray;

    private static string Excerpt(string text, int length = 40) => text.Length == 0 ? "paragraphe sans texte" : text.Length > length ? text[..length] + "…" : text;

    private static IEnumerable<Inline> Diff(string old, string now)
    {
        int head = 0; while (head < old.Length && head < now.Length && old[head] == now[head]) head++;
        int tail = 0; while (tail < old.Length - head && tail < now.Length - head && old[^(tail + 1)] == now[^(tail + 1)]) tail++;
        if (head > 0) yield return new Run(head > 24 ? "…" + old[(head - 24)..head] : old[..head]);
        if (old.Length - head - tail > 0) yield return new Run(old.Substring(head, old.Length - head - tail)) { Foreground = Brush.Parse("#C0392B"), TextDecorations = TextDecorations.Strikethrough };
        if (now.Length - head - tail > 0) yield return new Run(now.Substring(head, now.Length - head - tail)) { Foreground = Brush.Parse("#1E8E5A"), TextDecorations = TextDecorations.Underline };
        if (tail > 0) { var rest = old[^tail..]; yield return new Run(rest.Length > 24 ? rest[..24] + "…" : rest); }
    }

    private void GoTo(string paragraph) { if (boxes.TryGetValue(paragraph, out var box)) { active = paragraph; box.Focus(); box.BringIntoView(); } }

    private void RenderReview()
    {
        var comments = LiveList("comments"); var revisions = LiveList("revisions"); var paragraphs = Document.Snapshot["content"]!["paragraphs"]!.AsArray();
        if (!reviewOpenedOnce) { reviewOpenedOnce = true; if (comments.Count + revisions.Count > 0) reviewOpen = true; }
        string TextOf(string id) => paragraphs.FirstOrDefault(p => p!["id"]!.GetValue<string>() == id)?["text"]?.GetValue<string>() ?? "";
        var signature = string.Join("\u0001", comments.Select(n => n["id"] + "\u0002" + n["paragraph"] + "\u0002" + n["text"] + "\u0002" + n["resolved"])) + "\u0003" + string.Join("\u0001", revisions.Select(n => n["id"] + "\u0002" + n["paragraph"] + "\u0002" + TextOf(n["paragraph"]!.GetValue<string>()))) + "\u0003" + tracking + reviewOpen;
        if (signature == reviewSignature) return; reviewSignature = signature;
        var open = comments.Count(c => c["resolved"]?.GetValue<bool>() != true);
        this.FindControl<TextBlock>("CommentSummary")!.Text = comments.Count == 0 ? "Aucun commentaire" : open == 0 ? "Tous les commentaires sont résolus" : open == 1 ? "1 commentaire ouvert" : $"{open} commentaires ouverts";
        this.FindControl<TextBlock>("RevisionSummary")!.Text = revisions.Count == 0 ? "Aucune modification suivie" : revisions.Count == 1 ? "1 modification suivie" : $"{revisions.Count} modifications suivies";
        this.FindControl<TextBlock>("TrackChangesLabel")!.Text = tracking ? "Suivi des modifications : activé" : "Suivi des modifications : désactivé";
        this.FindControl<Button>("TrackChangesButton")!.Background = tracking ? Brush.Parse(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light ? "#DCE8FC" : "#426497") : Brushes.Transparent;
        this.FindControl<Button>("AcceptAllButton")!.IsEnabled = revisions.Count > 0; this.FindControl<Button>("RejectAllButton")!.IsEnabled = revisions.Count > 0;
        var pane = this.FindControl<Border>("ReviewPane")!; pane.IsVisible = reviewOpen;
        this.FindControl<TextBlock>("ReviewPaneSummary")!.Text = $"{comments.Count} commentaire{(comments.Count > 1 ? "s" : "")} · {revisions.Count} modification{(revisions.Count > 1 ? "s" : "")} suivie{(revisions.Count > 1 ? "s" : "")}";
        var list = this.FindControl<StackPanel>("ReviewList")!; list.Children.Clear();
        if (comments.Count + revisions.Count == 0) list.Children.Add(new TextBlock { Text = tracking ? "Le suivi est actif : vos modifications de texte apparaîtront ici." : "Aucun commentaire ni modification suivie. Utilisez « Nouveau commentaire » ou activez le suivi des modifications.", TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12, Margin = new Thickness(4) });
        TextBox? focus = null;
        for (int i = 0; i < comments.Count; i++)
        {
            var comment = comments[i]; var id = comment["id"]!.GetValue<string>(); var resolved = comment["resolved"]?.GetValue<bool>() == true; var paragraph = comment["paragraph"]!.GetValue<string>();
            var when = DateTimeOffset.TryParse(comment["date"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.GetCultureInfo("fr-FR")) : "";
            var box = new TextBox { Name = "Comment_" + i, Text = comment["text"]!.GetValue<string>(), Watermark = "Écrivez votre commentaire", AcceptsReturn = true, MaxLength = 5000, FontSize = 12, TextWrapping = TextWrapping.Wrap, MinHeight = 48 };
            box.TextChanged += (_, _) => { if (!rendering) { commentDrafts[id] = box.Text ?? ""; DraftUpdated(); } };
            box.LostFocus += async (_, _) => { await FlushCommentsAsync(); DraftUpdated(); };
            var resolve = new Button { Name = "ResolveComment_" + i, Content = resolved ? "Rouvrir" : "Résoudre", Classes = { "toolText" } }; resolve.Click += async (_, _) => await ChangeCommentAsync(id, n => n["resolved"] = !resolved);
            var remove = new Button { Name = "RemoveComment_" + i, Content = "Supprimer", Classes = { "toolText" } }; remove.Click += async (_, _) => { commentDrafts.Remove(id); await FlushCommentsAsync(); await SetListAsync("SetComments", "comments", LiveList("comments").Where(n => n["id"]!.GetValue<string>() != id)); };
            var go = new Button { Content = "« " + Excerpt(TextOf(paragraph)) + " »", Classes = { "quiet" }, HorizontalContentAlignment = HorizontalAlignment.Left, FontSize = 11, Opacity = .75 }; go.Click += (_, _) => GoTo(paragraph);
            var card = new StackPanel { Spacing = 4, Children = { new TextBlock { Text = comment["author"]!.GetValue<string>() + (when.Length > 0 ? " · " + when : "") + (resolved ? " · résolu" : ""), FontWeight = FontWeight.SemiBold, FontSize = 12 }, go, box, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { resolve, remove } } } };
            list.Children.Add(new Border { Child = card, Padding = new Thickness(10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = LineBrush(), Opacity = resolved ? .6 : 1 });
            if (id == focusComment) focus = box;
        }
        for (int i = 0; i < revisions.Count; i++)
        {
            var revision = revisions[i]; var id = revision["id"]!.GetValue<string>(); var paragraph = revision["paragraph"]!.GetValue<string>(); var insert = revision["kind"]!.GetValue<string>() == "insert";
            var when = DateTimeOffset.TryParse(revision["date"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.GetCultureInfo("fr-FR")) : "";
            var diff = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            if (insert) diff.Inlines!.Add(new Run(Excerpt(TextOf(paragraph), 80)) { Foreground = Brush.Parse("#1E8E5A"), TextDecorations = TextDecorations.Underline });
            else foreach (var run in Diff(revision["old"]!.GetValue<string>(), TextOf(paragraph))) diff.Inlines!.Add(run);
            var accept = new Button { Name = "AcceptRevision_" + i, Content = "Accepter", Classes = { "toolText" } }; accept.Click += async (_, _) => await ResolveRevisionsAsync(id, true);
            var reject = new Button { Name = "RejectRevision_" + i, Content = "Refuser", Classes = { "toolText" } }; reject.Click += async (_, _) => await ResolveRevisionsAsync(id, false);
            var go = new Button { Content = insert ? "Nouveau paragraphe" : "Texte modifié", Classes = { "quiet" }, HorizontalContentAlignment = HorizontalAlignment.Left, FontSize = 11, Opacity = .75 }; go.Click += (_, _) => GoTo(paragraph);
            var card = new StackPanel { Spacing = 4, Children = { new TextBlock { Text = revision["author"]!.GetValue<string>() + (when.Length > 0 ? " · " + when : ""), FontWeight = FontWeight.SemiBold, FontSize = 12 }, go, diff, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { accept, reject } } } };
            list.Children.Add(new Border { Child = card, Padding = new Thickness(10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = LineBrush() });
        }
        if (focus is not null) { focusComment = null; Avalonia.Threading.Dispatcher.UIThread.Post(() => focus.Focus(), Avalonia.Threading.DispatcherPriority.Input); }
    }
}
