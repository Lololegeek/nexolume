using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Nexolume.Desktop.Editors.Writer.Controls;
using Nexolume.Desktop.Editors.Writer.Spelling;

namespace Nexolume.Desktop.Editors.Writer.Views;

public partial class WriterView
{
    private readonly SpellingService spelling = new();
    private readonly Dictionary<string, SpellingAdorner> spellingMarks = [];
    private readonly Dictionary<string, (string Text, string Language, SpellingResult Result)> spellingCache = [];
    private readonly HashSet<string> ignoredSpelling = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer spellingTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private Task spellingTask = Task.CompletedTask;
    private int spellingTaskGeneration;
    private int spellingGeneration;
    private bool spellingDetached;
    private bool rebuildingSpelling;
    private sealed record LocatedIssue(string Id, int Paragraph, string Text, SpellingIssue Issue)
    {
        public override string ToString() => $"{Issue.Word}  ·  § {Paragraph}";
    }

    private void SetupSpelling()
    {
        var languages = this.FindControl<ComboBox>("SpellingLanguage")!;
        languages.ItemsSource = new[] { "Français (France)", "English (United States)" };
        languages.SelectedIndex = Writing.SpellingLanguage == "en-US" ? 1 : 0;
        void LanguageLabel() => this.FindControl<TextBlock>("WritingLanguageLabel")!.Text = (Writing.SpellingLanguage == "fr-FR" ? "Français" : "English") + " · Format du paragraphe actif";
        LanguageLabel();
        languages.SelectionChanged += (_, _) => { Writing.SpellingLanguage = languages.SelectedIndex == 1 ? "en-US" : "fr-FR"; LanguageLabel(); SaveWriting(); ScheduleSpelling(); };
        this.FindControl<Button>("SpellingButton")!.Click += async (_, _) => await ShowSpellingAsync();
        this.FindControl<Button>("CloseSpellingButton")!.Click += (_, _) => this.FindControl<Border>("SpellingPanel")!.IsVisible = false;
        this.FindControl<ListBox>("SpellingIssues")!.SelectionChanged += (_, _) => { if (!rebuildingSpelling) SelectSpellingIssue(); };
        this.FindControl<Button>("IgnoreSpellingButton")!.Click += (_, _) => AcceptSpelling(false);
        this.FindControl<Button>("AddDictionaryButton")!.Click += (_, _) => AcceptSpelling(true);
        var dictionaryList = new ListBox { MinHeight = 60, MaxHeight = 230 };
        var removeWord = new Button { Content = "Retirer le mot sélectionné", IsEnabled = false };
        dictionaryList.SelectionChanged += (_, _) => removeWord.IsEnabled = dictionaryList.SelectedItem is string;
        void RefreshDictionary() => dictionaryList.ItemsSource = Writing.Dictionary.TryGetValue(Writing.SpellingLanguage, out var entries) ? entries.OrderBy(w => w, StringComparer.CurrentCultureIgnoreCase).ToArray() : Array.Empty<string>();
        removeWord.Click += (_, _) => { if (dictionaryList.SelectedItem is string word && Writing.Dictionary.TryGetValue(Writing.SpellingLanguage, out var entries)) { entries.Remove(word); SaveWriting(); RefreshDictionary(); ApplySpellingResults(); } };
        var dictionaryFlyout = new Flyout { Content = new StackPanel { Width = 270, Spacing = 12, Children =
        {
            new TextBlock { Text = "Dictionnaire personnel", FontSize = 17, FontWeight = Avalonia.Media.FontWeight.SemiBold },
            new TextBlock { Text = "Mots acceptés pour la langue choisie. Retirez un mot pour le vérifier à nouveau.", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "caption" } },
            dictionaryList, removeWord
        }}};
        dictionaryFlyout.Opened += (_, _) => RefreshDictionary();
        this.FindControl<Button>("ManageDictionaryButton")!.Flyout = dictionaryFlyout;
        var underline = this.FindControl<CheckBox>("UnderlineSpellingToggle")!; underline.IsChecked = Writing.UnderlineSpelling;
        underline.IsCheckedChanged += (_, _) => { Writing.UnderlineSpelling = underline.IsChecked == true; SaveWriting(); ApplySpellingResults(); };
        spellingTimer.Tick += async (_, _) => { spellingTimer.Stop(); await RefreshSpellingAsync(); };
        AddHandler(KeyDownEvent, async (_, e) => { if (e.Key == Key.F7 && e.KeyModifiers == KeyModifiers.None) { e.Handled = true; await ShowSpellingAsync(); } }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }
    private void SaveWriting() { if (ReferenceEquals(Writing, WritingOptions.Current)) Writing.Save(); }
    private void ScheduleSpelling()
    {
        if (spellingDetached) return;
        spellingGeneration++; spellingTimer.Stop(); spellingTimer.Start();
    }
    public async Task ShowSpellingAsync()
    {
        this.FindControl<Border>("SearchPanel")!.IsVisible = false;
        this.FindControl<Border>("NavigationPanel")!.IsVisible = false;
        this.FindControl<Border>("SpellingPanel")!.IsVisible = true;
        await FlushAsync(); await RefreshSpellingAsync(); SelectSpellingIssue();
    }
    public async Task RefreshSpellingAsync()
    {
        spellingTimer.Stop();
        while (!spellingDetached)
        {
            if (spellingTask.IsCompleted) { spellingTaskGeneration = spellingGeneration; spellingTask = CheckSpellingCoreAsync(); }
            await spellingTask;
            if (spellingTaskGeneration == spellingGeneration) return;
        }
    }
    private async Task CheckSpellingCoreAsync()
    {
        var generation = spellingGeneration; var language = Writing.SpellingLanguage;
        try
        {
            foreach (var (id, box) in boxes.ToArray())
            {
                var text = box.Text ?? "";
                if (spellingCache.TryGetValue(id, out var cached) && cached.Text == text && cached.Language == language) continue;
                var result = await spelling.CheckAsync(text, language);
                if (spellingDetached || generation != spellingGeneration || box.Text != text) return;
                spellingCache[id] = (text, language, result);
            }
            if (!spellingDetached) ApplySpellingResults();
        }
        finally { if (!spellingDetached && generation != spellingGeneration) { spellingTimer.Stop(); spellingTimer.Start(); } }
    }
    private bool Accepted(SpellingIssue issue) => ignoredSpelling.Contains(Writing.SpellingLanguage + ":" + issue.Word) ||
        (Writing.Dictionary.TryGetValue(Writing.SpellingLanguage, out var words) && words.Contains(issue.Word, StringComparer.OrdinalIgnoreCase));
    private void ApplySpellingResults()
    {
        var located = new List<LocatedIssue>(); string? unavailable = null; var index = 0;
        foreach (var paragraph in Document.Snapshot["content"]!["paragraphs"]!.AsArray())
        {
            var id = paragraph!["id"]!.GetValue<string>(); if (!boxes.TryGetValue(id, out var box)) continue;
            index++;
            if (!spellingCache.TryGetValue(id, out var cached) || cached.Text != box.Text || cached.Language != Writing.SpellingLanguage) { spellingMarks[id].SetIssues([]); continue; }
            if (!cached.Result.Available) unavailable = cached.Result.Message;
            var issues = cached.Result.Issues.Where(issue => !Accepted(issue)).ToArray();
            spellingMarks[id].SetIssues(Writing.UnderlineSpelling ? issues : []);
            located.AddRange(issues.Select(issue => new LocatedIssue(id, index, cached.Text, issue)));
        }
        var list = this.FindControl<ListBox>("SpellingIssues")!;
        var old = list.SelectedItem as LocatedIssue;
        rebuildingSpelling = true;
        list.ItemsSource = located;
        if (located.Count > 0) list.SelectedItem = located.FirstOrDefault(i => i.Id == old?.Id && i.Issue.Start == old.Issue.Start) ?? located[0];
        rebuildingSpelling = false;
        SelectSpellingIssue(false);
        this.FindControl<TextBlock>("SpellingStatus")!.Text = unavailable ?? (located.Count == 0 ? "Aucun signalement dans le texte vérifié." : $"{located.Count} signalement(s) · 200 maximum par paragraphe");
    }
    private void SelectSpellingIssue(bool focus = true)
    {
        var suggestions = this.FindControl<StackPanel>("SpellingSuggestions")!; suggestions.Children.Clear();
        var selected = this.FindControl<ListBox>("SpellingIssues")!.SelectedItem as LocatedIssue;
        this.FindControl<Button>("IgnoreSpellingButton")!.IsEnabled = selected is not null;
        this.FindControl<Button>("AddDictionaryButton")!.IsEnabled = selected is not null;
        if (selected is null || !boxes.TryGetValue(selected.Id, out var box) || box.Text != selected.Text) return;
        if (focus && this.FindControl<Border>("SpellingPanel")!.IsVisible)
        {
            active = selected.Id; box.Focus(); box.CaretIndex = selected.Issue.Start + selected.Issue.Length;
            box.SelectionStart = selected.Issue.Start; box.SelectionEnd = selected.Issue.Start + selected.Issue.Length; box.BringIntoView();
        }
        foreach (var suggestion in selected.Issue.Suggestions)
        {
            var button = new Button { Content = suggestion.Length == 0 ? "Supprimer la répétition" : suggestion, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left };
            button.Click += async (_, _) => await CorrectSpellingAsync(selected.Id, selected.Text, selected.Issue.Start, selected.Issue.Length, suggestion);
            suggestions.Children.Add(button);
        }
        if (suggestions.Children.Count == 0) suggestions.Children.Add(new TextBlock { Text = "Aucune suggestion. Vous pouvez modifier le mot dans le document.", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "caption" } });
    }
    public async Task CorrectSpellingAsync(string id, string expectedText, int start, int length, string replacement)
    {
        if (!boxes.TryGetValue(id, out var box) || box.Text != expectedText || start < 0 || length < 0 || start > expectedText.Length - length) return;
        await FlushAsync();
        if (box.Text != expectedText) return;
        group = Guid.NewGuid().ToString();
        await Enqueue(() => Document.ExecuteAsync("ReplaceText", new { id, start, end = start + length, text = replacement }));
        box.Focus(); box.CaretIndex = start + replacement.Length;
        await RefreshSpellingAsync();
    }
    private void AcceptSpelling(bool permanent)
    {
        if (this.FindControl<ListBox>("SpellingIssues")!.SelectedItem is not LocatedIssue selected) return;
        if (permanent)
        {
            if (!Writing.Dictionary.TryGetValue(Writing.SpellingLanguage, out var words)) Writing.Dictionary[Writing.SpellingLanguage] = words = [];
            if (!words.Contains(selected.Issue.Word, StringComparer.OrdinalIgnoreCase)) words.Add(selected.Issue.Word);
            SaveWriting();
        }
        else ignoredSpelling.Add(Writing.SpellingLanguage + ":" + selected.Issue.Word);
        ApplySpellingResults();
    }
}
