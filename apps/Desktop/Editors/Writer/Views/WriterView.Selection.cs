using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Text.Json.Nodes;
using Nexolume.Desktop.Editors.Writer.Controls;
namespace Nexolume.Desktop.Editors.Writer.Views;

/// <summary>
/// Selection that spans several paragraphs. Each paragraph is its own text box, so the
/// document-level range lives here (anchor and focus as paragraph id + UTF-16 offset)
/// and is mirrored onto the boxes' own selections for display. Edits run as one engine batch.
/// </summary>
public partial class WriterView
{
    private (string Anchor, int AnchorOffset, string Focus, int FocusOffset)? multi;
    private bool dragging;
    private string? pressId;
    private int pressOffset;
    private (string Id, int Offset)? shiftAnchor;

    public bool HasMultiSelection => multi is not null;
    private JsonArray Paras => Document.Snapshot["content"]!["paragraphs"]!.AsArray();
    private static bool IsText(JsonNode? p) => p!["image"] is null && p["table"] is null;
    private string IdAt(int index) => Paras[index]!["id"]!.GetValue<string>();
    private int IndexOf(string id) { var list = Paras; for (int i = 0; i < list.Count; i++) if (list[i]!["id"]!.GetValue<string>() == id) return i; return -1; }
    private string TextOf(string id) => boxes.TryGetValue(id, out var box) ? box.Text ?? "" : "";
    private string? IdOfBox(Control? control) => control is RichParagraphBox box ? boxes.FirstOrDefault(pair => pair.Value == box).Key : null;
    private TextPresenter? PresenterOf(Control box) => box.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();

    private string? NeighbourText(string id, int direction)
    {
        var list = Paras; for (int i = IndexOf(id) + direction; i >= 0 && i < list.Count; i += direction) if (IsText(list[i])) return IdAt(i);
        return null;
    }
    private string? EdgeText(bool last)
    {
        var list = Paras; for (int k = 0; k < list.Count; k++) { var i = last ? list.Count - 1 - k : k; if (IsText(list[i])) return IdAt(i); }
        return null;
    }

    /// <summary>The selection in document order: first/last paragraph index and offsets.</summary>
    private (int First, int FirstOffset, int Last, int LastOffset)? MultiRange()
    {
        if (multi is not { } m) return null;
        int a = IndexOf(m.Anchor), f = IndexOf(m.Focus); if (a < 0 || f < 0) { multi = null; return null; }
        return a < f || (a == f && m.AnchorOffset <= m.FocusOffset) ? (a, m.AnchorOffset, f, m.FocusOffset) : (f, m.FocusOffset, a, m.AnchorOffset);
    }
    private IEnumerable<(string Id, int Start, int End, int Length)> Pieces((int First, int FirstOffset, int Last, int LastOffset) r)
    {
        var list = Paras;
        for (int i = r.First; i <= r.Last; i++)
        {
            if (!IsText(list[i])) continue;
            var length = list[i]!["text"]!.GetValue<string>().Length;
            yield return (IdAt(i), i == r.First ? Math.Min(r.FirstOffset, length) : 0, i == r.Last ? Math.Min(r.LastOffset, length) : length, length);
        }
    }

    private void ShowMulti()
    {
        var range = MultiRange(); if (range is not { } r) return;
        var list = Paras;
        for (int i = 0; i < list.Count; i++)
        {
            if (!IsText(list[i]) || !boxes.TryGetValue(IdAt(i), out var box)) continue;
            var length = (box.Text ?? "").Length; int start, end;
            if (i < r.First || i > r.Last) { start = end = 0; }
            else { start = i == r.First ? Math.Min(r.FirstOffset, length) : 0; end = i == r.Last ? Math.Min(r.LastOffset, length) : length; }
            if (box.SelectionStart != start) box.SelectionStart = start;
            if (box.SelectionEnd != end) box.SelectionEnd = end;
        }
    }
    private void BeginMulti(string anchor, int anchorOffset, string focus, int focusOffset)
    {
        if (anchor == focus) { ExitMulti(focus, Math.Min(anchorOffset, focusOffset), Math.Max(anchorOffset, focusOffset)); return; }
        multi = (anchor, anchorOffset, focus, focusOffset); active = focus;
        if (boxes.TryGetValue(focus, out var box) && !box.IsFocused) box.Focus();
        ShowMulti(); UpdateStyleButtons();
    }
    /// <summary>Leaves multi-paragraph mode; the kept paragraph holds the (possibly collapsed) selection.</summary>
    private void ExitMulti(string? keep, int start, int end)
    {
        multi = null;
        foreach (var (id, box) in boxes)
        {
            if (id == keep) continue;
            if (box.SelectionStart != 0) box.SelectionStart = 0;
            if (box.SelectionEnd != 0) box.SelectionEnd = 0;
        }
        if (keep is not null && boxes.TryGetValue(keep, out var kept))
        {
            active = keep; kept.SelectionStart = start; kept.SelectionEnd = end; kept.CaretIndex = end;
            if (!kept.IsFocused) kept.Focus();
        }
        UpdateStyleButtons();
    }
    public void SelectRange(int firstIndex, int firstOffset, int lastIndex, int lastOffset) => BeginMulti(IdAt(firstIndex), firstOffset, IdAt(lastIndex), lastOffset);
    public void SelectAllParagraphs()
    {
        var first = EdgeText(false); var last = EdgeText(true); if (first is null || last is null) return;
        if (first == last) { var box = boxes[first]; box.Focus(); box.SelectAll(); return; }
        BeginMulti(first, 0, last, TextOf(last).Length);
    }

    private static int StepLeft(string text, int offset, bool word)
    {
        var i = Math.Max(0, offset - 1); if (i > 0 && char.IsLowSurrogate(text[i]) && char.IsHighSurrogate(text[i - 1])) i--;
        if (!word) return i;
        while (i > 0 && char.IsWhiteSpace(text[i])) i--;
        while (i > 0 && !char.IsWhiteSpace(text[i - 1])) i--;
        return i;
    }
    private static int StepRight(string text, int offset, bool word)
    {
        var i = Math.Min(text.Length, offset + 1); if (i < text.Length && char.IsLowSurrogate(text[i]) && char.IsHighSurrogate(text[i - 1])) i++;
        if (!word) return i;
        while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return i;
    }
    private (string Id, int Offset) MoveVertical(string id, int offset, int direction)
    {
        var text = TextOf(id); offset = Math.Clamp(offset, 0, text.Length); double x = 0;
        var layout = PresenterOf(boxes[id])?.TextLayout;
        if (layout is not null && text.Length > 0)
        {
            var rect = layout.HitTestTextPosition(offset); var line = layout.GetLineIndexFromCharacterIndex(offset, false); x = rect.X;
            if (direction < 0 && line > 0) return (id, Math.Clamp(layout.HitTestPoint(new Point(x, rect.Top - 1)).TextPosition, 0, text.Length));
            if (direction > 0 && line < layout.TextLines.Count - 1) return (id, Math.Clamp(layout.HitTestPoint(new Point(x, rect.Bottom + 1)).TextPosition, 0, text.Length));
        }
        var other = NeighbourText(id, direction); if (other is null) return (id, direction < 0 ? 0 : text.Length);
        var target = TextOf(other); var otherLayout = PresenterOf(boxes[other])?.TextLayout;
        if (otherLayout is null || target.Length == 0) return (other, Math.Min(offset, target.Length));
        return (other, Math.Clamp(otherLayout.HitTestPoint(new Point(x, direction < 0 ? Math.Max(1, otherLayout.Height - 1) : 1)).TextPosition, 0, target.Length));
    }
    private (string Id, int Offset) MoveFocus(Key key, bool ctrl, string id, int offset)
    {
        var text = TextOf(id);
        switch (key)
        {
            case Key.Left: if (offset == 0) return NeighbourText(id, -1) is { } p ? (p, TextOf(p).Length) : (id, 0); return (id, StepLeft(text, offset, ctrl));
            case Key.Right: if (offset >= text.Length) return NeighbourText(id, 1) is { } n ? (n, 0) : (id, text.Length); return (id, StepRight(text, offset, ctrl));
            case Key.Up: return MoveVertical(id, offset, -1);
            case Key.Down: return MoveVertical(id, offset, 1);
            case Key.Home: return ctrl ? (EdgeText(false) ?? id, 0) : (id, 0);
            case Key.End: return ctrl ? EdgeText(true) is { } last ? (last, TextOf(last).Length) : (id, text.Length) : (id, text.Length);
            default: return (id, offset);
        }
    }

    private void SetupSelection()
    {
        AddHandler(KeyDownEvent, async (_, e) => await OnSelectionKeyAsync(e), RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, async (_, e) => { if (multi is null || string.IsNullOrEmpty(e.Text) || e.Text.All(char.IsControl)) return; e.Handled = true; await DeleteMultiAsync(e.Text); }, RoutingStrategies.Tunnel);
        var panel = this.FindControl<StackPanel>("ParagraphPanel")!;
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Source is not Visual source || !panel.IsVisualAncestorOf(source) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            shiftAnchor = null;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && active is not null && boxes.TryGetValue(active, out var current))
                shiftAnchor = multi is { } m ? (m.Anchor, m.AnchorOffset) : (active, current.SelectionStart != current.SelectionEnd ? current.SelectionStart : current.CaretIndex);
        }, RoutingStrategies.Tunnel, true);
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Source is not Visual source || !panel.IsVisualAncestorOf(source) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var id = IdOfBox(source as Control ?? source.FindAncestorOfType<RichParagraphBox>());
            if (id is null || !boxes.TryGetValue(id, out var box)) { if (multi is not null) ExitMulti(null, 0, 0); dragging = false; shiftAnchor = null; return; }
            if (shiftAnchor is { } anchor) { shiftAnchor = null; BeginMulti(anchor.Id, anchor.Offset, id, box.CaretIndex); dragging = false; return; }
            if (multi is not null) ExitMulti(id, box.CaretIndex, box.CaretIndex);
            dragging = true; pressId = id; pressOffset = box.CaretIndex;
        }, RoutingStrategies.Bubble, true);
        AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (!dragging || pressId is null) return;
            if (!e.GetCurrentPoint(panel).Properties.IsLeftButtonPressed) { dragging = false; return; }
            var position = e.GetPosition(panel); var children = panel.Children; if (children.Count == 0) return;
            int index = children.Count - 1; for (int i = 0; i < children.Count; i++) if (position.Y < children[i].Bounds.Bottom) { index = i; break; }
            var list = Paras; if (index >= list.Count) return;
            if (!IsText(list[index])) { var step = IndexOf(pressId) < index ? 1 : -1; var near = NeighbourText(IdAt(index), step) ?? NeighbourText(IdAt(index), -step); if (near is null) return; index = IndexOf(near); }
            var targetId = IdAt(index);
            if (targetId == pressId) { if (multi is not null) { var own = boxes[pressId]; ExitMulti(null, 0, 0); } return; }
            var target = boxes[targetId]; var presenter = PresenterOf(target); var text = target.Text ?? "";
            int offset = presenter?.TextLayout is { } layout && text.Length > 0 ? Math.Clamp(layout.HitTestPoint(e.GetPosition(presenter)).TextPosition, 0, text.Length) : (position.Y < target.Bounds.Top + ((Control)target.Parent!).Bounds.Top ? 0 : text.Length);
            var start = IndexOf(pressId);
            if (position.Y > ((Control)target.Parent!).Bounds.Bottom) offset = text.Length; else if (position.Y < ((Control)target.Parent!).Bounds.Top) offset = 0;
            BeginMulti(pressId, Math.Min(pressOffset, TextOf(pressId).Length), targetId, offset);
        }, RoutingStrategies.Bubble, true);
        AddHandler(PointerReleasedEvent, (_, _) => dragging = false, RoutingStrategies.Bubble, true);
    }

    private async Task OnSelectionKeyAsync(KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control); var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift); var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (alt) return;
        var inParagraph = e.Source is RichParagraphBox || (multi is not null && e.Source is not TextBox);
        if (ctrl && !shift && e.Key == Key.A && (e.Source is RichParagraphBox || multi is not null)) { e.Handled = true; SelectAllParagraphs(); return; }
        if (multi is not { } m)
        {
            if (!shift || e.Source is not RichParagraphBox source || IdOfBox(source) is not { } id) return;
            var caret = source.SelectionStart != source.SelectionEnd ? source.SelectionEnd : source.CaretIndex; var anchor = source.SelectionStart != source.SelectionEnd ? source.SelectionStart : source.CaretIndex;
            if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)) return;
            if (e.Key == Key.Left && caret > 0 || e.Key == Key.Right && caret < (source.Text ?? "").Length || e.Key is Key.Home or Key.End && !ctrl) return;
            var (target, offset) = MoveFocus(e.Key, ctrl, id, caret);
            if (target == id) return;
            e.Handled = true; BeginMulti(id, anchor, target, offset); return;
        }
        if (!inParagraph) return;
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        {
            e.Handled = true;
            if (shift)
            {
                var (target, offset) = MoveFocus(e.Key, ctrl, m.Focus, m.FocusOffset);
                if (target == m.Anchor) ExitMulti(target, Math.Min(m.AnchorOffset, offset), Math.Max(m.AnchorOffset, offset));
                else { multi = (m.Anchor, m.AnchorOffset, target, offset); active = target; if (boxes.TryGetValue(target, out var box) && !box.IsFocused) box.Focus(); ShowMulti(); UpdateStyleButtons(); }
                return;
            }
            var range = MultiRange()!.Value; var toStart = e.Key is Key.Left or Key.Up or Key.Home or Key.PageUp;
            var (id, offsetAt) = toStart ? (IdAt(range.First), range.FirstOffset) : (IdAt(range.Last), range.LastOffset);
            ExitMulti(id, offsetAt, offsetAt); return;
        }
        if (e.Key == Key.Escape) { e.Handled = true; ExitMulti(m.Focus, m.FocusOffset, m.FocusOffset); return; }
        if (e.Key is Key.Delete or Key.Back) { e.Handled = true; await DeleteMultiAsync(""); return; }
        if (e.Key == Key.Enter && !shift) { e.Handled = true; await DeleteMultiAsync("", true); return; }
        if (ctrl && e.Key == Key.C) { e.Handled = true; await CopyMultiAsync(false); return; }
        if (ctrl && e.Key == Key.X) { e.Handled = true; await CopyMultiAsync(true); return; }
        if (ctrl && e.Key == Key.V) { e.Handled = true; await PasteMultiAsync(); }
    }

    private bool TryMultiClipboard(string operation)
    {
        if (multi is null) return false;
        _ = operation switch { "copy" => CopyMultiAsync(false), "cut" => CopyMultiAsync(true), _ => PasteMultiAsync() };
        return true;
    }
    public string SelectedText()
    {
        if (MultiRange() is not { } r) return "";
        return string.Join("\n", Pieces(r).Select(piece => TextOf(piece.Id)[piece.Start..piece.End]));
    }
    public async Task CopyMultiAsync(bool cut)
    {
        if (multi is null) return;
        var text = SelectedText();
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        if (cut) await DeleteMultiAsync("");
    }
    public async Task PasteMultiAsync()
    {
        if (multi is null) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && await clipboard.TryGetTextAsync() is { } text) await DeleteMultiAsync(text);
    }

    /// <summary>Replaces the selection with <paramref name="insert"/> as one undoable step.</summary>
    public async Task DeleteMultiAsync(string insert, bool split = false)
    {
        if (multi is null) return;
        await FlushAsync();
        if (MultiRange() is not { } r) return;
        string firstId = IdAt(r.First), lastId = IdAt(r.Last); var firstLength = Paras[r.First]!["text"]!.GetValue<string>().Length; var lastLength = Paras[r.Last]!["text"]!.GetValue<string>().Length;
        int firstOffset = Math.Min(r.FirstOffset, firstLength), lastOffset = Math.Min(r.LastOffset, lastLength);
        var commands = new List<object>();
        if (lastOffset > 0) commands.Add(new { command = "ReplaceText", @params = new { id = lastId, start = 0, end = lastOffset, text = "" } });
        if (firstOffset < firstLength || insert.Length > 0) commands.Add(new { command = "ReplaceText", @params = new { id = firstId, start = firstOffset, end = firstLength, text = insert } });
        for (int i = r.Last - 1; i > r.First; i--) commands.Add(new { command = "DeleteParagraph", @params = new { id = IdAt(i) } });
        commands.Add(new { command = "MergeParagraph", @params = new { id = lastId } });
        var caret = firstOffset + insert.Length; var landing = firstId;
        if (split) { landing = Guid.NewGuid().ToString(); commands.Add(new { command = "SplitParagraph", @params = new { id = firstId, new_id = landing, start = caret, end = caret } }); caret = 0; }
        multi = null; group = Guid.NewGuid().ToString();
        await Enqueue(() => Document.ExecuteAsync("Batch", new { commands = commands.ToArray() }));
        if (LastError is not null) return;
        ExitMulti(landing, caret, caret);
    }

    /// <summary>Applies a character patch computed from the first selected character to every selected piece.</summary>
    private async Task ToggleMultiAsync(string property)
    {
        await FlushAsync(); if (MultiRange() is not { } r) return;
        var firstId = IdAt(r.First);
        var character = boxes[firstId].StyleAt(Math.Min(r.FirstOffset, TextOf(firstId).Length));
        await FormatPiecesAsync(r, ScriptPatch(property, !character[property]!.GetValue<bool>()));
    }
    private async Task ClearMultiFormattingAsync()
    {
        await FlushAsync(); if (MultiRange() is not { } r) return;
        await FormatPiecesAsync(r, new JsonObject { ["bold"] = false, ["italic"] = false, ["underline"] = false, ["strike"] = false, ["superscript"] = false, ["subscript"] = false, ["small_caps"] = false, ["font_family"] = "Aptos", ["font_size"] = 16, ["color"] = "#111111", ["highlight"] = "none" });
    }
    private async Task FormatPiecesAsync((int First, int FirstOffset, int Last, int LastOffset) r, JsonObject patch)
    {
        var commands = Pieces(r).Where(p => p.Start < p.End).Select(p => (object)new { command = "FormatText", @params = new { id = p.Id, start = p.Start, end = p.End, style = patch } }).ToArray();
        if (commands.Length == 0) return;
        group = Guid.NewGuid().ToString();
        await Enqueue(() => Document.ExecuteAsync("Batch", new { commands }));
        ShowMulti();
    }
    private async Task ChangeStyleMultiAsync(Action<JsonNode> change, bool wholeParagraph)
    {
        await FlushAsync(); if (MultiRange() is not { } r) return;
        var firstId = IdAt(r.First); var first = Paras[r.First]!; var before = first["style"]!.DeepClone();
        var firstBox = boxes[firstId]; if (firstBox.Runs.Count > 0) foreach (var property in firstBox.StyleAt(Math.Min(r.FirstOffset, TextOf(firstId).Length))) before[property.Key] = property.Value!.DeepClone();
        var style = before.DeepClone(); change(style);
        var patch = new JsonObject(); foreach (var key in new[] { "bold", "italic", "underline", "strike", "superscript", "subscript", "small_caps", "font_family", "font_size", "color", "highlight" }) if (!JsonNode.DeepEquals(before[key], style[key])) patch[key] = style[key]!.DeepClone();
        var commands = new List<object>(); var paragraphs = Paras;
        foreach (var piece in Pieces(r))
        {
            if (!wholeParagraph && patch.Count > 0 && piece.Start < piece.End) commands.Add(new { command = "FormatText", @params = new { id = piece.Id, start = piece.Start, end = piece.End, style = patch } });
            else { var paragraphStyle = paragraphs[IndexOf(piece.Id)]!["style"]!.DeepClone(); change(paragraphStyle); commands.Add(new { command = "SetParagraph", @params = new { id = piece.Id, style = paragraphStyle } }); }
        }
        if (commands.Count == 0) return;
        group = Guid.NewGuid().ToString();
        await Enqueue(() => Document.ExecuteAsync("Batch", new { commands = commands.ToArray() }));
        ShowMulti();
    }
    private async Task ChangeCaseMultiAsync(string mode)
    {
        await FlushAsync(); if (MultiRange() is not { } r) return;
        var commands = new List<object>();
        foreach (var piece in Pieces(r))
        {
            if (piece.Start >= piece.End) continue; var text = TextOf(piece.Id)[piece.Start..piece.End]; var replacement = TypingAssistance.ChangeCase(text, mode);
            if (replacement != text) commands.Add(new { command = "ReplaceText", @params = new { id = piece.Id, start = piece.Start, end = piece.End, text = replacement } });
        }
        if (commands.Count == 0) return;
        group = Guid.NewGuid().ToString();
        await Enqueue(() => Document.ExecuteAsync("Batch", new { commands = commands.ToArray() }));
        // Case changes can alter lengths: keep the selection on the same start and the new end.
        ShowMulti();
    }
}

public partial class WriterView
{
    /// <summary>Sorts the text paragraphs of the selection (or of the whole document) A→Z by rewriting their text and styles, as one undo step.</summary>
    public async Task SortParagraphsAsync()
    {
        await FlushAsync(); var list = Paras;
        int from = 0, to = list.Count - 1;
        if (MultiRange() is { } r) { from = r.First; to = r.Last; }
        var slots = Enumerable.Range(from, to - from + 1).Where(i => IsText(list[i])).ToList();
        if (slots.Count < 2) { SetStatus("Sélectionnez au moins deux paragraphes à trier."); return; }
        var sorted = slots.OrderBy(i => list[i]!["text"]!.GetValue<string>(), StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("fr-FR"), true)).ToList();
        if (sorted.SequenceEqual(slots)) return;
        var commands = new List<object>();
        for (int k = 0; k < slots.Count; k++)
        {
            if (sorted[k] == slots[k]) continue; var source = list[sorted[k]]!;
            commands.Add(new { command = "SetParagraph", @params = new { id = IdAt(slots[k]), text = source["text"]!.GetValue<string>(), style = source["style"]!.DeepClone(), runs = source["runs"]?.DeepClone() ?? new JsonArray() } });
        }
        multi = null; group = Guid.NewGuid().ToString();
        await Enqueue(() => Document.ExecuteAsync("Batch", new { commands = commands.ToArray() }));
    }

    public async Task RemoveEmptyParagraphsAsync()
    {
        await FlushAsync(); var list = Paras;
        var empty = Enumerable.Range(0, list.Count).Where(i => IsText(list[i]) && string.IsNullOrEmpty(list[i]!["text"]!.GetValue<string>())).ToList();
        if (empty.Count == list.Count) empty.RemoveAt(0);
        if (empty.Count == 0) { SetStatus("Aucun paragraphe vide."); return; }
        multi = null; group = Guid.NewGuid().ToString();
        await Enqueue(() => Document.ExecuteAsync("Batch", new { commands = empty.Select(i => (object)new { command = "DeleteParagraph", @params = new { id = IdAt(i) } }).ToArray() }));
    }
}

public partial class WriterView
{
    /// <summary>Joins the selected text paragraphs into the first one, separated by spaces.</summary>
    public async Task JoinParagraphsAsync()
    {
        await FlushAsync(); if (MultiRange() is not { } r) { SetStatus("Sélectionnez plusieurs paragraphes à fusionner."); return; }
        var list = Paras; var ids = Enumerable.Range(r.First, r.Last - r.First + 1).Where(i => IsText(list[i])).ToList();
        if (ids.Count < 2) return;
        var text = string.Join(" ", ids.Select(i => list[i]!["text"]!.GetValue<string>().Trim()).Where(t => t.Length > 0));
        var commands = new List<object> { new { command = "ReplaceText", @params = new { id = IdAt(ids[0]), start = 0, end = list[ids[0]]!["text"]!.GetValue<string>().Length, text } } };
        foreach (var i in ids.Skip(1).Reverse()) commands.Add(new { command = "DeleteParagraph", @params = new { id = IdAt(i) } });
        var keep = IdAt(ids[0]); multi = null; group = Guid.NewGuid().ToString();
        await Enqueue(() => Document.ExecuteAsync("Batch", new { commands = commands.ToArray() }));
        ExitMulti(keep, 0, 0);
    }

    public async Task ReverseParagraphsAsync()
    {
        await FlushAsync(); if (MultiRange() is not { } r) { SetStatus("Sélectionnez plusieurs paragraphes à inverser."); return; }
        var list = Paras; var slots = Enumerable.Range(r.First, r.Last - r.First + 1).Where(i => IsText(list[i])).ToList();
        if (slots.Count < 2) return;
        var commands = new List<object>();
        for (int k = 0; k < slots.Count / 2; k++)
        {
            int a = slots[k], b = slots[slots.Count - 1 - k]; var pa = list[a]!; var pb = list[b]!;
            commands.Add(new { command = "SetParagraph", @params = new { id = IdAt(a), text = pb["text"]!.GetValue<string>(), style = pb["style"]!.DeepClone(), runs = pb["runs"]?.DeepClone() ?? new JsonArray() } });
            commands.Add(new { command = "SetParagraph", @params = new { id = IdAt(b), text = pa["text"]!.GetValue<string>(), style = pa["style"]!.DeepClone(), runs = pa["runs"]?.DeepClone() ?? new JsonArray() } });
        }
        multi = null; group = Guid.NewGuid().ToString();
        await Enqueue(() => Document.ExecuteAsync("Batch", new { commands = commands.ToArray() }));
    }
}
