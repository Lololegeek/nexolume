using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nexolume.Desktop.Editors.Writer;
using Nexolume.Desktop.Editors.Writer.Views;
using OfficeBridge.Services;

internal static class WritingChecks
{
    public static void Run()
    {
        using var engine = new OfficeEngine(); using var doc = Program.Get(DocumentSession.CreateAsync(engine, "writer", "Aide à la rédaction"));
        var options = new WritingOptions(); var view = new WriterView(doc, options);
        var window = new Window { Content = view, Width = 1200, Height = 900 }; window.Show(); Dispatcher.UIThread.RunJobs();
        TextBox Box() => view.FindControl<StackPanel>("ParagraphPanel")!.GetVisualDescendants().OfType<TextBox>().Last();
        var box = Box();
        string Text() => doc.Snapshot["content"]!["paragraphs"]!.AsArray().Last()!["text"]!.GetValue<string>();
        void Reset(string text = "") { box.Focus(); box.Text = text; box.CaretIndex = text.Length; box.SelectionStart = box.SelectionEnd = text.Length; Program.Pump(view.FlushAsync()); }
        void Type(string text) { box.Focus(); foreach (var ch in text) { window.KeyTextInput(ch.ToString()); Dispatcher.UIThread.RunJobs(); Program.Pump(view.Pending); } Program.Pump(view.FlushAsync()); }
        Type("bonjour. deja très bien! ca marche? oui ");
        Program.Check(Text() == "Bonjour. Déjà très bien! Ça marche? Oui ", "French sentence capitals and replacements: " + Text());
        Reset(); Type("bonjoru "); Program.Check(Text() == "Bonjour ", "Common typo preserves capital");
        window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None); Dispatcher.UIThread.RunJobs(); Program.Pump(view.FlushAsync());
        Program.Check(Text() == "Bonjoru ", "Backspace restores raw word and delimiter");
        Type("x"); Program.Check(Text() == "Bonjoru x", "Dismissed correction stays dismissed");
        Reset(); Type("BOnjour NASA XML CA et COdeur "); Program.Check(Text() == "Bonjour NASA XML CA et Codeur ", "Double capitals and acronym protection: " + Text());
        Reset(); Type("l'amour... (c) (tm) -> "); Program.Check(Text() == "L’amour… © ™ → ", "Smart typography: " + Text());
        Reset("Voir "); Type("https://exemple.fr/deja www.site.fr/ca nom@deja.fr C:\\deja\\ca ");
        Program.Check(Text() == "Voir https://exemple.fr/deja www.site.fr/ca nom@deja.fr C:\\deja\\ca ", "URLs email paths left intact");
        Reset(); Type("M. dupont Dr. martin 3.14 et un mot "); Program.Check(Text() == "M. dupont Dr. martin 3.14 et un mot ", "Abbreviations and decimals");
        Reset(); window.KeyTextInput("bonjour deja bonjoru..."); Dispatcher.UIThread.RunJobs(); Program.Pump(view.FlushAsync());
        Program.Check(Text() == "bonjour deja bonjoru...", "Bulk input not rewritten");
        Reset("préfixe deja suffixe"); box.SelectionStart = 8; box.SelectionEnd = 12; window.KeyTextInput("été"); Dispatcher.UIThread.RunJobs(); Program.Pump(view.FlushAsync());
        Program.Check(Text() == "préfixe été suffixe", "Selection and multi-character composition");
        options.SentenceCapitals = options.CommonTypos = options.DoubleCapitals = options.Typography = false;
        Reset(); Type("bonjoru. deja BOnjour l'amour..."); Program.Check(Text() == "bonjoru. deja BOnjour l'amour...", "Options disable every rewrite");
        options.SentenceCapitals = options.CommonTypos = options.DoubleCapitals = options.Typography = true;
        Reset("Un texte été 🦀"); box.SelectionStart = 3; box.SelectionEnd = 8;
        Program.Pump(view.ChangeCaseAsync("upper")); Program.Check(Text() == "Un TEXTE été 🦀", "Change case respects UTF16 selection");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Check(Text() == "Un texte été 🦀", "Case transformation is undoable"); Program.Pump(doc.ExecuteAsync("Redo"));
        Reset("été unicode 🦀"); box.SelectAll();
        window.KeyPressQwerty(PhysicalKey.F3, RawInputModifiers.Shift); Dispatcher.UIThread.RunJobs(); Program.Pump(view.Pending);
        Program.Check(Text() == "Été Unicode 🦀", "Shift F3 title case");
        window.KeyPressQwerty(PhysicalKey.F3, RawInputModifiers.Shift); Dispatcher.UIThread.RunJobs(); Program.Pump(view.Pending);
        Program.Check(Text() == "ÉTÉ UNICODE 🦀", "Shift F3 uppercase cycle preserves selection");
        window.KeyPressQwerty(PhysicalKey.F3, RawInputModifiers.Shift); Dispatcher.UIThread.RunJobs(); Program.Pump(view.Pending);
        Program.Check(Text() == "été unicode 🦀", "Shift F3 lowercase cycle");
        Reset("ÉTÉ 🦀. déjà VU! oui"); Program.Click(view, "ReviewRibbonTab"); Program.Click(view, "SentenceCaseButton"); Program.Pump(view.Pending);
        Program.Check(Text() == "Été 🦀. Déjà vu! Oui", "Sentence case ribbon command on whole paragraph");
        Reset(); Type("* ");
        Program.Check(Text() == "" && doc.Snapshot["content"]!["paragraphs"]![0]!["style"]!["list"]!.GetValue<string>() == "bullet", "Automatic bullet list");
        Type("premier point"); window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); Dispatcher.UIThread.RunJobs(); Program.Pump(view.Pending); box = Box();
        Program.Check(doc.Snapshot["content"]!["paragraphs"]!.AsArray().Count == 2 && doc.Snapshot["content"]!["paragraphs"]![1]!["style"]!["list"]!.GetValue<string>() == "bullet", "List continues with Enter");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); Dispatcher.UIThread.RunJobs(); Program.Pump(view.Pending);
        Program.Check(doc.Snapshot["content"]!["paragraphs"]!.AsArray().Count == 2 && doc.Snapshot["content"]!["paragraphs"]![1]!["style"]!["list"]!.GetValue<string>() == "none", "Empty list Enter exits without extra paragraph");
        Type("1. "); Program.Check(doc.Snapshot["content"]!["paragraphs"]![1]!["style"]!["list"]!.GetValue<string>() == "number", "Automatic numbered list");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Check(Text() == "1.", "List conversion undo preserves typed marker");
        Program.Click(view, "ReviewRibbonTab"); var settingsButton = view.FindControl<Button>("AutoCorrectButton")!; var at = settingsButton.TranslatePoint(new Avalonia.Point(30, 16), window)!.Value; window.MouseDown(at, MouseButton.Left); window.MouseUp(at, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        var flyout = (Flyout)view.FindControl<Button>("AutoCorrectButton")!.Flyout!;
        var popup = (ScrollViewer)flyout.Content!;
        var check = popup.GetVisualDescendants().OfType<CheckBox>().First(); check.IsChecked = false;
        Program.Check(!options.SentenceCapitals, "Options flyout updates actual typing options");
        var fields = popup.GetVisualDescendants().OfType<TextBox>().ToArray(); fields[0].Text = "bjr"; fields[1].Text = "bonjour à tous";
        var add = popup.GetVisualDescendants().OfType<Button>().First(b => b.Name == "AddReplacementButton");
        add.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        Program.Check(options.Replacements["bjr"] == "bonjour à tous", "Custom replacement saved from UI");
        System.Threading.Thread.Sleep(180); Dispatcher.UIThread.RunJobs();
        var presenter = (Control)popup.Parent!;
        using (var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)presenter.Bounds.Width, (int)presenter.Bounds.Height))) { image.Render(presenter); image.Save(Path.Combine(Program.Artifacts, "writer-autocorrection-options.png")); }
        flyout.Hide(); Reset(); Type("bjr "); Program.Check(Text() == "bonjour à tous ", "Custom replacement applies during typing");
        options.SentenceCapitals = true;
        Reset("deja"); window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); Dispatcher.UIThread.RunJobs(); Program.Pump(view.Pending); box = Box();
        Program.Check(doc.Snapshot["content"]!["paragraphs"]!.AsArray()[^2]!["text"]!.GetValue<string>() == "déjà", "Corrections also apply on Enter: " + doc.Snapshot["content"]!.ToJsonString());
        Program.Capture(window, "writer-review");
        var originalTheme = Application.Current!.RequestedThemeVariant;
        foreach (var light in new[] { false, true })
        {
            Application.Current.RequestedThemeVariant = light ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
            foreach (var width in new[] { 1000, 1440, 1920 })
            {
                window.Width = width; window.Height = width == 1000 ? 720 : 950; Dispatcher.UIThread.RunJobs();
                Program.Capture(window, $"writer-review-{(light ? "light" : "dark")}-{width}");
                flyout.ShowAt(settingsButton); Dispatcher.UIThread.RunJobs();
                Program.Check(flyout.IsOpen && popup.Bounds.Width < width - 30 && popup.Bounds.Height <= 490 && popup.Extent.Width <= popup.Viewport.Width + 1, "Settings popup fits window"); flyout.Hide();
            }
        }
        Application.Current.RequestedThemeVariant = originalTheme;
        var path = Path.Combine(Program.Artifacts, "writing.oswriter"); Program.Pump(doc.SaveAsync(path)); using var reopened = Program.Get(DocumentSession.OpenAsync(engine, path));
        Program.Check(System.Text.Json.Nodes.JsonNode.DeepEquals(doc.Snapshot["content"], reopened.Snapshot["content"]), "Assisted text native roundtrip");
        view.Detach(); window.Close();
        Console.WriteLine("PASS Writer assistance: live French corrections, capitals, acronym/URL protection, typography, revert, case selection/undo, lists, options and native roundtrip");
    }
}
