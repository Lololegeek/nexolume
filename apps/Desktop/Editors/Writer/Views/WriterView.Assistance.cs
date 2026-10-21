using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using System.Text.Json.Nodes;

namespace Nexolume.Desktop.Editors.Writer.Views;

public partial class WriterView
{
    public WritingOptions Writing { get; }
    private (TextBox Box, string Before, string After, int Caret)? lastCorrection;

    private void SetupAssistance()
    {
        var content = new StackPanel { Spacing = 12, Width = 338, Margin = new Avalonia.Thickness(6) };
        content.Children.Add(new TextBlock { Text = "Pendant la saisie", FontSize = 18, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        void Option(string name, string label, Func<bool> get, Action<bool> set)
        {
            var check = new CheckBox { Name = name, Content = label, IsChecked = get() };
            check.IsCheckedChanged += (_, _) => { set(check.IsChecked == true); if (ReferenceEquals(Writing, WritingOptions.Current)) Writing.Save(); };
            content.Children.Add(check);
        }
        Option("SentenceCapitalsOption", "Majuscule en début de phrase", () => Writing.SentenceCapitals, v => Writing.SentenceCapitals = v);
        Option("DoubleCapitalsOption", "Corriger deux majuscules initiales", () => Writing.DoubleCapitals, v => Writing.DoubleCapitals = v);
        Option("CommonTyposOption", "Corriger les fautes courantes en français", () => Writing.CommonTypos, v => Writing.CommonTypos = v);
        Option("TypographyOption", "Apostrophes, points de suspension, symboles", () => Writing.Typography, v => Writing.Typography = v);
        Option("AutomaticListsOption", "Créer une liste avec * ou 1. puis Espace", () => Writing.AutomaticLists, v => Writing.AutomaticLists = v);
        content.Children.Add(new TextBlock { Text = "Retour arrière rétablit la dernière correction. Les textes collés et les documents ouverts ne sont pas corrigés automatiquement.", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "caption" } });
        content.Children.Add(new TextBlock { Text = "Corrections courantes : bonjoru → bonjour, beaucoups → beaucoup, deja → déjà, coeur → cœur… Ce réglage n’est pas un correcteur grammatical complet.", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "caption" } });
        var custom = new StackPanel { Spacing = 10 };
        custom.Children.Add(new TextBlock { Text = "Vos remplacements", FontSize = 18, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        var from = new TextBox { Name = "ReplacementFrom", Watermark = "Ex. : bjr", MaxLength = 40 };
        var to = new TextBox { Name = "ReplacementTo", Watermark = "Ex. : bonjour", MaxLength = 200 };
        custom.Children.Add(new TextBlock { Text = "Remplacer ce mot", Classes = { "caption" } }); custom.Children.Add(from);
        custom.Children.Add(new TextBlock { Text = "Par", Classes = { "caption" } }); custom.Children.Add(to);
        var add = new Button { Name = "AddReplacementButton", Content = "Ajouter le remplacement", IsEnabled = false };
        var feedback = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "caption" } };
        var entries = new StackPanel { Spacing = 6 };
        void Save() { if (ReferenceEquals(Writing, WritingOptions.Current)) Writing.Save(); }
        void Refresh()
        {
            entries.Children.Clear();
            foreach (var pair in Writing.Replacements.OrderBy(p => p.Key))
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
                row.Children.Add(new TextBlock { Text = pair.Key + " → " + pair.Value, TextWrapping = Avalonia.Media.TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                var remove = new Button { Content = "×", Classes = { "quiet" } }; ToolTip.SetTip(remove, "Supprimer le remplacement de « " + pair.Key + " »");
                remove.Click += (_, _) => { Writing.Replacements.Remove(pair.Key); Save(); Refresh(); };
                Grid.SetColumn(remove, 1); row.Children.Add(remove); entries.Children.Add(row);
            }
        }
        void Validate() => add.IsEnabled = System.Text.RegularExpressions.Regex.IsMatch(from.Text ?? "", @"^[\p{L}][\p{L}’']{0,39}$") && !string.IsNullOrWhiteSpace(to.Text) && Writing.Replacements.Count < 100;
        from.TextChanged += (_, _) => Validate(); to.TextChanged += (_, _) => Validate();
        add.Click += (_, _) =>
        {
            var key = (from.Text ?? "").ToLowerInvariant();
            foreach (var old in Writing.Replacements.Keys.Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToArray()) Writing.Replacements.Remove(old);
            Writing.Replacements[key] = to.Text!.Trim(); Save(); Refresh(); feedback.Text = "Remplacement enregistré. Il s’applique à la fin du mot."; from.Text = ""; to.Text = ""; from.Focus();
        };
        custom.Children.Add(add); custom.Children.Add(feedback); custom.Children.Add(entries); Refresh();
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("350,300"), ColumnSpacing = 28, Margin = new Avalonia.Thickness(8) };
        columns.Children.Add(content); Grid.SetColumn(custom, 1); columns.Children.Add(custom);
        this.FindControl<Button>("AutoCorrectButton")!.Flyout = new Flyout { FlyoutPresenterClasses = { "writingOptions" }, Content = new ScrollViewer { Content = columns, Width = 694, MaxHeight = 490, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } };
        foreach (var (button, mode) in new[] { ("UpperCaseButton", "upper"), ("LowerCaseButton", "lower"), ("SentenceCaseButton", "sentence"), ("TitleCaseButton", "title") })
            this.FindControl<Button>(button)!.Click += async (_, _) => await ChangeCaseAsync(mode);
    }

    private void HookAssistance(string id, TextBox box)
    {
        box.LostFocus += (_, _) => lastCorrection = null;
        box.AddHandler(TextInputEvent, async (_, e) =>
        {
            if (rendering || string.IsNullOrEmpty(e.Text) || e.Text.Length != 1) { lastCorrection = null; return; }
            var start = Math.Min(box.SelectionStart, box.SelectionEnd); var end = Math.Max(box.SelectionStart, box.SelectionEnd);
            var old = box.Text ?? ""; var raw = old[..start] + e.Text + old[end..]; var caret = start + e.Text.Length;
            lastCorrection = null;
            if (Writing.AutomaticLists && e.Text == " " && caret == raw.Length && raw is "* " or "- " or "1. ")
            {
                var paragraph = Document.Snapshot["content"]!["paragraphs"]!.AsArray().First(p => p!["id"]!.GetValue<string>() == id)!;
                if (paragraph["style"]!["list"]!.GetValue<string>() == "none")
                {
                    e.Handled = true; await FlushAsync();
                    var style = paragraph["style"]!.DeepClone(); style["list"] = raw == "1. " ? "number" : "bullet";
                    group = Guid.NewGuid().ToString();
                    await Enqueue(() => Document.ExecuteAsync("SetParagraph", new { id, text = "", style }));
                    box.Focus(); box.CaretIndex = 0; return;
                }
            }
            var corrected = TypingAssistance.Correct(raw, caret, e.Text, Writing);
            if (corrected == raw) return;
            e.Handled = true;
            box.Text = corrected; var nextCaret = caret + corrected.Length - raw.Length;
            box.SelectionStart = box.SelectionEnd = box.CaretIndex = nextCaret;
            lastCorrection = (box, raw, corrected, nextCaret);
        }, RoutingStrategies.Tunnel);
        box.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None && lastCorrection is { } correction && correction.Box == box && box.Text == correction.After && box.CaretIndex == correction.Caret && box.SelectionStart == box.SelectionEnd)
            {
                e.Handled = true; lastCorrection = null; box.Text = correction.Before;
                box.SelectionStart = box.SelectionEnd = box.CaretIndex = correction.Caret + correction.Before.Length - correction.After.Length;
            }
            else lastCorrection = null;
        }, RoutingStrategies.Tunnel);
    }

    private void CorrectBeforeParagraphBreak(string id, TextBox box)
    {
        var start = Math.Min(box.SelectionStart, box.SelectionEnd); var end = Math.Max(box.SelectionStart, box.SelectionEnd);
        var text = box.Text ?? ""; var raw = text[..start] + "\n" + text[end..];
        var corrected = TypingAssistance.Correct(raw, start + 1, "\n", Writing);
        if (corrected == raw) return;
        var caret = start + corrected.Length - raw.Length;
        box.Text = corrected.Remove(caret, 1); box.CaretIndex = caret; box.SelectionStart = box.SelectionEnd = caret;
        // TextChanged is dispatched later by Avalonia. Make this draft available to
        // the immediate flush before SplitParagraph so it cannot save stale text.
        drafts[id] = box.Text; DraftUpdated();
    }

    public async Task ChangeCaseAsync(string mode)
    {
        if (multi is not null) { await ChangeCaseMultiAsync(mode); return; }
        if (active is null || !boxes.TryGetValue(active, out var box)) return;
        var paragraphId = active;
        var start = Math.Min(box.SelectionStart, box.SelectionEnd); var end = Math.Max(box.SelectionStart, box.SelectionEnd);
        var text = box.Text ?? ""; var selection = start != end;
        if (!selection) { start = 0; end = text.Length; }
        var replacement = TypingAssistance.ChangeCase(text[start..end], mode);
        var runs = box is Controls.RichParagraphBox rich ? rich.Runs.DeepClone() : new JsonArray();
        await FlushAsync(); group = Guid.NewGuid().ToString(); lastCorrection = null;
        if (replacement.Length == end - start) await Enqueue(() => Document.ExecuteAsync("SetParagraph", new { id = paragraphId, text = text[..start] + replacement + text[end..], runs }));
        else await Enqueue(() => Document.ExecuteAsync("ReplaceText", new { id = paragraphId, start, end, text = replacement }));
        box.Focus(); box.CaretIndex = start + replacement.Length; box.SelectionStart = start; box.SelectionEnd = start + replacement.Length;
    }
}
