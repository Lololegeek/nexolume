using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nexolume.Desktop.Editors.Writer;
using Nexolume.Desktop.Editors.Writer.Controls;
using Nexolume.Desktop.Editors.Writer.Spelling;
using Nexolume.Desktop.Editors.Writer.Views;
using OfficeBridge.Services;

internal static class ReviewChecks
{
    public static void Search()
    {
        using var engine = new OfficeEngine(); using var doc = Program.Get(DocumentSession.CreateAsync(engine, "writer", "Recherche avancée"));
        var view = new WriterView(doc, new WritingOptions()); var window = new Window { Content = view, Width = 1200, Height = 900 }; window.Show(); Dispatcher.UIThread.RunJobs();
        TextBox Box() => view.FindControl<StackPanel>("ParagraphPanel")!.GetVisualDescendants().OfType<TextBox>().Last();
        var first = Box(); const string original = "Chat chaton CHAT chat 🦀chat 𐐀chat chat\u0301";
        first.Text = original; Dispatcher.UIThread.RunJobs(); Program.Pump(view.FlushAsync());
        Program.Click(view, "SearchButton"); var query = view.FindControl<TextBox>("FindInput")!; query.Text = "chat";
        view.FindControl<CheckBox>("MatchCaseToggle")!.IsChecked = false; view.FindControl<CheckBox>("WholeWordToggle")!.IsChecked = true; Dispatcher.UIThread.RunJobs();
        Program.Check(view.FindControl<TextBlock>("FindStatus")!.Text!.StartsWith("4 occurrences"), "Unicode whole words exclude prefixes, astral letters and combining marks");
        Program.Pump(view.FindNextAsync(true)); Program.Check(first.SelectionStart == original.IndexOf("chat 𐐀", StringComparison.Ordinal), "Previous search starts at last whole word");
        Program.Pump(view.FindNextAsync()); Program.Check(first.SelectedText == "Chat", "Next wraps to first case-insensitive result");
        view.FindControl<TextBox>("ReplaceInput")!.Text = "chien"; Program.Pump(view.ReplaceOneAsync());
        Program.Check(first.Text == "chien" + original[4..] && first.SelectedText == "CHAT", "Replace one edits selection then selects next result");
        Program.Pump(view.ReplaceOneAsync()); Program.Check(first.Text!.Contains("chaton chien chat"), "Replace next occurrence");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Check(first.Text == "chien" + original[4..], "One undo restores only previous replacement");
        Program.Pump(view.AddParagraphAsync()); var second = Box(); second.Text = "Un CHAT et chaton."; Dispatcher.UIThread.RunJobs(); Program.Pump(view.FlushAsync());
        view.FindControl<TextBox>("ReplaceInput")!.Text = "félin 🦀"; Program.Pump(view.ReplaceAllAsync());
        Program.Check(first.Text == "chien chaton félin 🦀 félin 🦀 🦀félin 🦀 𐐀chat chat\u0301" && second.Text == "Un félin 🦀 et chaton.", "Whole-word replace all spans paragraphs with Unicode replacement");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Check(first.Text == "chien" + original[4..] && second.Text == "Un CHAT et chaton.", "One undo restores every changed paragraph");
        Program.Pump(doc.ExecuteAsync("Redo")); Program.Pump(doc.ExecuteAsync("Undo"));
        query.Text = "chat"; view.FindControl<CheckBox>("MatchCaseToggle")!.IsChecked = true; Dispatcher.UIThread.RunJobs(); Program.Pump(view.FindNextAsync());
        Program.Check(first.SelectedText == "chat", "Case-sensitive search excludes CHAT");
        Program.Capture(window, "writer-search-advanced");
        second.Text = string.Concat(Enumerable.Repeat("a ", 10001)); Dispatcher.UIThread.RunJobs(); Program.Pump(view.FlushAsync());
        query.Text = "a"; view.FindControl<TextBox>("ReplaceInput")!.Text = "b"; Dispatcher.UIThread.RunJobs(); Program.Pump(view.ReplaceAllAsync());
        Program.Check(second.Text == string.Concat(Enumerable.Repeat("b ", 10001)), "Replace all covers matches beyond 10000 navigation cap");
        query.Text = ""; Dispatcher.UIThread.RunJobs(); Program.Check(!view.FindControl<Button>("ReplaceOneButton")!.IsEnabled, "Empty query cannot replace");
        view.Detach(); window.Close(); Console.WriteLine("PASS Writer advanced search: case/Unicode whole words, previous/wrap, selected replacement, multi-paragraph atomic undo and more than 10000 matches");
    }
    public static void Spelling()
    {
        var service = new SpellingService(); var result = Program.Get(service.CheckAsync("Bonjour, je suiis contant de rédiger ce documant.", "fr-FR"));
        Program.Check(result.Available && result.Issues.Count > 0, "Real French Windows dictionary unavailable or returned no errors: " + result.Message);
        Program.Check(result.Issues.Any(i => i.Suggestions.Count > 0), "Native spelling suggestions");
        var missing = Program.Get(service.CheckAsync("test", "zz-ZZ")); Program.Check(!missing.Available && missing.Message is not null, "Missing dictionary explained");
        var unicode = Program.Get(service.CheckAsync("été 🦀 suiis", "fr-FR")); Program.Check(unicode.Issues.Any(i => i.Start == 7 && i.Word == "suiis"), "Windows errors use correct UTF16 offsets after emoji");
        using var engine = new OfficeEngine(); using var doc = Program.Get(DocumentSession.CreateAsync(engine, "writer", "Relecture"));
        var options = new WritingOptions(); var view = new WriterView(doc, options); var window = new Window { Content = view, Width = 1440, Height = 950 }; window.Show(); Dispatcher.UIThread.RunJobs();
        var box = view.FindControl<StackPanel>("ParagraphPanel")!.GetVisualDescendants().OfType<TextBox>().First();
        box.Text = "Bonjour, je suiis contant de rédiger ce documant."; Dispatcher.UIThread.RunJobs(); Program.Pump(view.FlushAsync()); Program.Pump(view.ShowSpellingAsync());
        var issues = view.FindControl<ListBox>("SpellingIssues")!; Program.Check(issues.ItemCount > 0, "Spelling sidebar lists real issues");
        Program.Check(view.FindControl<Border>("SpellingPanel")!.TranslatePoint(default, window)!.Value.X >= 1100, "Spelling sidebar occupies its own right column");
        Program.Check(view.FindControl<ScrollViewer>("PageScroll")!.Bounds.Width < 1200, "Spelling sidebar leaves document space");
        Program.Click(view, "ReviewRibbonTab"); Program.Capture(window, "writer-spelling");
        window.Width = 1000; window.Height = 720; Dispatcher.UIThread.RunJobs(); Program.Capture(window, "writer-spelling-compact"); window.Width = 1440; window.Height = 950;
        var mark = ((Grid)box.Parent!).Children.OfType<SpellingAdorner>().Single(); Program.Check(mark.Issues.Count > 0, "Live underline ranges");
        var before = box.Text!; var first = mark.Issues[0]; var id = doc.Snapshot["content"]!["paragraphs"]![0]!["id"]!.GetValue<string>();
        var suggestion = first.Suggestions.First();
        var buttons = view.FindControl<StackPanel>("SpellingSuggestions")!.Children.OfType<Button>().ToArray();
        Program.Check(buttons.Length > 0, "Sidebar suggestion controls");
        buttons[0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); Program.Pump(view.Pending); Program.Pump(view.RefreshSpellingAsync());
        Program.Check(box.Text == before[..first.Start] + suggestion + before[(first.Start + first.Length)..], "Suggestion click edits actual document");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Check(box.Text == before, "Spelling correction undo");
        Program.Pump(view.RefreshSpellingAsync());
        var count = issues.ItemCount; Program.Click(view, "IgnoreSpellingButton"); Program.Check(issues.ItemCount < count, "Ignore applies within document");
        Program.Click(view, "AddDictionaryButton"); Program.Check(options.Dictionary["fr-FR"].Count == 1, "Nexolume personal dictionary");
        var dictionaryFlyout = (Flyout)view.FindControl<Button>("ManageDictionaryButton")!.Flyout!; dictionaryFlyout.ShowAt(view.FindControl<Button>("ManageDictionaryButton")!); Dispatcher.UIThread.RunJobs();
        var dictionaryControls = (StackPanel)dictionaryFlyout.Content!; var personal = dictionaryControls.Children.OfType<ListBox>().Single(); personal.SelectedIndex = 0;
        dictionaryControls.Children.OfType<Button>().Single().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Program.Check(options.Dictionary["fr-FR"].Count == 0 && issues.ItemCount > 0, "Personal dictionary removal restores spelling issue"); dictionaryFlyout.Hide();
        view.FindControl<CheckBox>("UnderlineSpellingToggle")!.IsChecked = false; Program.Check(mark.Issues.Count == 0 && !options.UnderlineSpelling, "Disable squiggles");
        view.FindControl<CheckBox>("UnderlineSpellingToggle")!.IsChecked = true;
        Program.Pump(view.CorrectSpellingAsync(id, "stale text", 0, 5, "wrong")); Program.Check(box.Text == before, "Stale suggestion cannot overwrite new text");
        Program.Click(view, "CloseSpellingButton"); box.Focus(); window.KeyPressQwerty(PhysicalKey.F7, RawInputModifiers.None); Dispatcher.UIThread.RunJobs(); Program.Pump(view.RefreshSpellingAsync());
        Program.Check(view.FindControl<Border>("SpellingPanel")!.IsVisible, "F7 opens actual spelling pane");
        Program.Click(view, "FocusModeButton"); Program.Check(!view.FindControl<Border>("SpellingPanel")!.IsVisible, "Focus mode hides spelling pane");
        Program.Click(view, "FocusExitButton"); Program.Check(view.FindControl<Border>("SpellingPanel")!.IsVisible, "Focus exit restores pane");
        view.FindControl<ComboBox>("SpellingLanguage")!.SelectedIndex = 1; Program.Pump(view.RefreshSpellingAsync());
        Program.Check(view.FindControl<TextBlock>("WritingLanguageLabel")!.Text!.StartsWith("English") && options.SpellingLanguage == "en-US", "Language change updates state and status");
        view.FindControl<ComboBox>("SpellingLanguage")!.SelectedIndex = 0; Program.Pump(view.RefreshSpellingAsync());
        var originalTheme = Application.Current!.RequestedThemeVariant;
        foreach (var light in new[] { false, true })
        {
            Application.Current.RequestedThemeVariant = light ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
            foreach (var width in new[] { 1000, 1440, 1920 }) { window.Width = width; window.Height = width == 1000 ? 720 : 950; Dispatcher.UIThread.RunJobs(); Program.Capture(window, $"writer-spelling-{(light ? "light" : "dark")}-{width}"); }
        }
        Application.Current.RequestedThemeVariant = originalTheme;
        view.Detach(); window.Close();
        Console.WriteLine("PASS Writer spelling: real Windows French dictionary, suggestions/UTF16 offsets, missing language, underlines, sidebar corrections/undo, ignore, personal words and stale-result protection");
    }
}
