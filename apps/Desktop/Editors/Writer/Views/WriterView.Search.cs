using Avalonia.Controls;
using System.Text;

namespace Nexolume.Desktop.Editors.Writer.Views;

public partial class WriterView
{
    private int searchPosition = -1;
    private bool MatchCase => this.FindControl<CheckBox>("MatchCaseToggle")!.IsChecked == true;
    private bool WholeWords => this.FindControl<CheckBox>("WholeWordToggle")!.IsChecked == true;
    private void SetupSearch()
    {
        this.FindControl<Button>("SearchButton")!.Click += (_, _) => ShowSearch(false);
        this.FindControl<Button>("CloseSearchButton")!.Click += (_, _) => this.FindControl<Border>("SearchPanel")!.IsVisible = false;
        void Reset() { searchPosition = -1; UpdateSearchStatus(); }
        this.FindControl<TextBox>("FindInput")!.TextChanged += (_, _) => Reset();
        this.FindControl<CheckBox>("MatchCaseToggle")!.IsCheckedChanged += (_, _) => Reset();
        this.FindControl<CheckBox>("WholeWordToggle")!.IsCheckedChanged += (_, _) => Reset();
        this.FindControl<Button>("FindNextButton")!.Click += async (_, _) => await FindNextAsync();
        this.FindControl<Button>("FindPreviousButton")!.Click += async (_, _) => await FindNextAsync(true);
        this.FindControl<Button>("ReplaceOneButton")!.Click += async (_, _) => await ReplaceOneAsync();
        this.FindControl<Button>("ReplaceAllButton")!.Click += async (_, _) => await ReplaceAllAsync();
    }
    private void ShowSearch(bool replace)
    {
        this.FindControl<Border>("SpellingPanel")!.IsVisible = false;
        this.FindControl<Border>("NavigationPanel")!.IsVisible = false;
        this.FindControl<Border>("SearchPanel")!.IsVisible = true;
        this.FindControl<TextBox>(replace ? "ReplaceInput" : "FindInput")!.Focus(); UpdateSearchStatus();
    }
    private static bool WordAt(string text, int index)
    {
        if (index < 0 || index >= text.Length) return false;
        if (char.IsLowSurrogate(text[index]) && index > 0 && char.IsHighSurrogate(text[index - 1])) index--;
        var rune = Rune.GetRuneAt(text, index);
        return Rune.IsLetterOrDigit(rune) || rune.Value == '_' || Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark;
    }
    private List<(string Id, int Offset, int Length)> Matches(bool limit = true)
    {
        var query = this.FindControl<TextBox>("FindInput")!.Text ?? ""; var result = new List<(string, int, int)>();
        if (query.Length == 0) return result;
        foreach (var p in Document.Snapshot["content"]!["paragraphs"]!.AsArray())
        {
            var text = p!["text"]!.GetValue<string>();
            for (int pos = 0; (pos = text.IndexOf(query, pos, MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)) >= 0; pos += query.Length)
            {
                if (WholeWords && (WordAt(text, pos - 1) || WordAt(text, pos + query.Length))) continue;
                result.Add((p["id"]!.GetValue<string>(), pos, query.Length)); if ((limit && result.Count >= 10000) || (!limit && result.Count > 100000)) return result;
            }
        }
        return result;
    }
    private void UpdateSearchStatus()
    {
        var count = Matches().Count;
        this.FindControl<TextBlock>("FindStatus")!.Text = $"{count}{(count == 10000 ? "+" : "")} occurrence{(count != 1 ? "s" : "")} · {(MatchCase ? "casse respectée" : "casse ignorée")}";
        foreach (var name in new[] { "FindNextButton", "FindPreviousButton", "ReplaceOneButton", "ReplaceAllButton" }) this.FindControl<Button>(name)!.IsEnabled = count > 0;
    }
    public async Task FindNextAsync(bool previous = false)
    {
        await FlushAsync(); var matches = Matches(); if (matches.Count == 0) { UpdateSearchStatus(); return; }
        searchPosition = previous ? (searchPosition < 0 ? matches.Count - 1 : (searchPosition - 1 + matches.Count) % matches.Count) : (searchPosition + 1) % matches.Count;
        SelectMatch(matches[searchPosition]); this.FindControl<TextBlock>("FindStatus")!.Text = $"Occurrence {searchPosition + 1} sur {matches.Count}";
    }
    private void SelectMatch((string Id, int Offset, int Length) match)
    {
        active = match.Id; var box = boxes[match.Id]; box.Focus(); box.CaretIndex = match.Offset + match.Length;
        box.SelectionStart = match.Offset; box.SelectionEnd = match.Offset + match.Length; box.BringIntoView();
    }
    public async Task ReplaceOneAsync()
    {
        await FlushAsync(); var matches = Matches(); if (matches.Count == 0) return;
        var index = Math.Clamp(searchPosition, 0, matches.Count - 1); var match = matches[index]; var box = boxes[match.Id];
        // The first press selects a match; replacing requires its exact selection.
        if (active != match.Id || Math.Min(box.SelectionStart, box.SelectionEnd) != match.Offset || Math.Abs(box.SelectionEnd - box.SelectionStart) != match.Length)
        { searchPosition = index; SelectMatch(match); return; }
        var replacement = this.FindControl<TextBox>("ReplaceInput")!.Text ?? ""; var text = box.Text ?? "";
        group = Guid.NewGuid().ToString();
        await Enqueue(() => Document.ExecuteAsync("ReplaceText", new { id = match.Id, start = match.Offset, end = match.Offset + match.Length, text = replacement }));
        searchPosition = -1; UpdateSearchStatus(); var next = Matches();
        var order = Document.Snapshot["content"]!["paragraphs"]!.AsArray().Select(p => p!["id"]!.GetValue<string>()).ToList();
        var nextIndex = next.FindIndex(m => order.IndexOf(m.Id) > order.IndexOf(match.Id) || (m.Id == match.Id && m.Offset >= match.Offset + replacement.Length));
        if (next.Count > 0) { searchPosition = nextIndex < 0 ? 0 : nextIndex; SelectMatch(next[searchPosition]); }
    }
    public async Task ReplaceAllAsync()
    {
        await FlushAsync(); var replacement = this.FindControl<TextBox>("ReplaceInput")!.Text ?? "";
        var matches = Matches(false); if (matches.Count == 0) return; var groups = matches.GroupBy(m => m.Id).ToArray();
        if (matches.Count > 100000) { this.FindControl<TextBlock>("FindStatus")!.Text = "Limite : 100 000 occurrences par remplacement. Aucune modification effectuée."; return; }
        if (groups.Length > 1000) { this.FindControl<TextBlock>("FindStatus")!.Text = "Limite : 1 000 paragraphes par remplacement."; return; }
        var commands = new List<object>();
        foreach (var paragraph in groups)
        {
            var text = boxes[paragraph.Key].Text ?? ""; var builder = new StringBuilder(); var offset = 0;
            foreach (var match in paragraph) { builder.Append(text, offset, match.Offset - offset); builder.Append(replacement); offset = match.Offset + match.Length; }
            builder.Append(text, offset, text.Length - offset); var changed = builder.ToString();
            if (Encoding.UTF8.GetByteCount(changed) > 2_000_000) { this.FindControl<TextBlock>("FindStatus")!.Text = "Le remplacement dépasserait la taille maximale d’un paragraphe."; return; }
            commands.Add(new { command = "ReplaceRanges", @params = new { id = paragraph.Key, edits = paragraph.Select(match => new { start = match.Offset, end = match.Offset + match.Length, text = replacement }).ToArray() } });
        }
        group = Guid.NewGuid().ToString(); await Enqueue(() => Document.ExecuteAsync("Batch", new { commands }));
        searchPosition = -1; UpdateSearchStatus();
        if (LastError is null) this.FindControl<TextBlock>("FindStatus")!.Text = $"{matches.Count} remplacement(s) effectué(s).";
    }
}
