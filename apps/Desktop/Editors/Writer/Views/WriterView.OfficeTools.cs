using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using System.Text.Json.Nodes;
namespace Nexolume.Desktop.Editors.Writer.Views;

public partial class WriterView
{
    private readonly Dictionary<string, ComboBox> officeInputs = [];
    private void SetupOfficeTools()
    {
        void Choices(string name, (string Label, string Value)[] values, Action<JsonNode, string> apply) { var box = this.FindControl<ComboBox>(name)!; officeInputs[name] = box; foreach (var (label, value) in values) { Control content = new TextBlock { Text = label }; if (value.StartsWith('#')) content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { new Border { Width = 13, Height = 13, Background = Brush.Parse(value), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2) }, content } }; box.Items.Add(new ComboBoxItem { Tag = value, Content = content }); } box.SelectionChanged += async (_, _) => { if (!updatingFont && box.SelectedItem is ComboBoxItem item) await ChangeStyleAsync(style => apply(style, (string)item.Tag!)); }; }
        Choices("FontColorBox", [("Noir", "#111111"), ("Bleu", "#285CB3"), ("Rouge", "#B3263D"), ("Vert", "#237459")], (s, v) => s["color"] = v);
        Choices("ShadingBox", [("Aucune", "none"), ("Gris clair", "#EDEDED"), ("Jaune pâle", "#FFF2CC"), ("Vert pâle", "#E2F0D9"), ("Bleu pâle", "#DDEBF7"), ("Rose pâle", "#FCE4EC")], (s, v) => s["shading"] = v);
        Choices("BorderBox", [("Aucune", "none"), ("Encadré", "box"), ("Trait dessous", "bottom"), ("Trait à gauche", "left")], (s, v) => s["border"] = v);
        Choices("HighlightBox", [("Aucun", "none"), ("Jaune", "#FFF1A8"), ("Vert clair", "#D8F2D9"), ("Bleu clair", "#DAE8FF")], (s, v) => s["highlight"] = v);
        Choices("LineSpacingBox", [("Simple", "1"), ("1,15", "1.15"), ("1,5", "1.5"), ("Double", "2")], (s, v) => s["line_spacing"] = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture));
        var after = this.FindControl<NumericUpDown>("AfterSpacingBox")!; after.ValueChanged += async (_, _) => { if (!updatingFont && after.Value is { } value) await ChangeStyleAsync(s => s["spacing_after"] = (double)value); };
        this.FindControl<Button>("BulletButton")!.Click += async (_, _) => await ChangeStyleAsync(s => s["list"] = s["list"]!.GetValue<string>() == "bullet" ? "none" : "bullet");
        this.FindControl<Button>("NumberButton")!.Click += async (_, _) => await ChangeStyleAsync(s => s["list"] = s["list"]!.GetValue<string>() == "number" ? "none" : "number");
        this.FindControl<Button>("IndentMoreButton")!.Click += async (_, _) => await ChangeStyleAsync(s => s["indent"] = Math.Min(8, s["indent"]!.GetValue<int>() + 1));
        this.FindControl<Button>("IndentLessButton")!.Click += async (_, _) => await ChangeStyleAsync(s => s["indent"] = Math.Max(0, s["indent"]!.GetValue<int>() - 1));
        this.FindControl<Button>("AlignJustifyButton")!.Click += async (_, _) => await ChangeStyleAsync(s => s["alignment"] = "justify");
        this.FindControl<Button>("ClearFormatButton")!.Click += async (_, _) => await ClearFormattingAsync();
        this.FindControl<Button>("SortButton")!.Click += async (_, _) => await SortParagraphsAsync();
        this.FindControl<Button>("RemoveEmptyButton")!.Click += async (_, _) => await RemoveEmptyParagraphsAsync();
        this.FindControl<Button>("JoinButton")!.Click += async (_, _) => await JoinParagraphsAsync();
        this.FindControl<Button>("ReverseButton")!.Click += async (_, _) => await ReverseParagraphsAsync();
        var toggle = this.FindControl<Button>("RibbonToggleButton")!; toggle.Click += (_, _) => { var border = this.FindControl<Border>("RibbonBorder")!; border.IsVisible = !border.IsVisible; toggle.Content = border.IsVisible ? "Masquer le ruban" : "Afficher le ruban"; };
        this.FindControl<Button>("FitPageButton")!.Click += (_, _) => { var width = this.FindControl<ScrollViewer>("PageScroll")!.Bounds.Width; this.FindControl<Slider>("ZoomSlider")!.Value = Math.Clamp((width - 80) / Printing.PageSettings.Read(Document.Snapshot).Width * 100, 50, 150); };
        var compactMenu = new MenuFlyout(); foreach (var (label, name) in new[] { ("Normal", "NormalStyleButton"), ("Titre 1", "HeadingStyleButton"), ("Titre 2", "SubtitleStyleButton") }) { var item = new MenuItem { Header = label }; item.Click += (_, _) => this.FindControl<Button>(name)!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); compactMenu.Items.Add(item); }
        this.FindControl<Button>("StylesCompactButton")!.Flyout = compactMenu;
        SizeChanged += (_, _) => { var compact = Bounds.Width < 1260; this.FindControl<StackPanel>("StyleGallery")!.IsVisible = !compact; this.FindControl<Button>("StylesCompactButton")!.IsVisible = compact; };
        SetupStyles(); SetupInsertTools(); SetupImages(); SetupTables();
        SetupSearch(); SetupSelection(); SetupLinks(); SetupFootnotes(); SetupReview();
        AddHandler(KeyDownEvent, async (_, e) => { if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return; if (e.Key == Key.K) { e.Handled = true; ShowLinkFlyout(); return; } if (e.Key is Key.F or Key.H) { e.Handled = true; ShowSearch(e.Key == Key.H); return; } if (e.Key is Key.B or Key.I or Key.U) { e.Handled = true; await StyleAsync(e.Key switch { Key.B => "bold", Key.I => "italic", _ => "underline" }); } }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }
    private void UpdateOfficeInputs(JsonNode style) { foreach (var (name, key) in new[] { ("FontColorBox", "color"), ("HighlightBox", "highlight"), ("ShadingBox", "shading"), ("BorderBox", "border"), ("LineSpacingBox", "line_spacing") }) { var box = officeInputs[name]; var value = key == "line_spacing" ? style[key]!.GetValue<double>().ToString(System.Globalization.CultureInfo.InvariantCulture) : style[key]?.GetValue<string>() ?? "none"; box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == value); } this.FindControl<NumericUpDown>("AfterSpacingBox")!.Value = (decimal)style["spacing_after"]!.GetValue<double>(); }
    private void HookParagraphKeys(string id, TextBox box)
    {
        box.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Key.F3 && e.KeyModifiers == KeyModifiers.Shift)
            {
                e.Handled = true;
                var start = Math.Min(box.SelectionStart, box.SelectionEnd); var end = Math.Max(box.SelectionStart, box.SelectionEnd);
                var text = start == end ? box.Text ?? "" : (box.Text ?? "")[start..end];
                var letters = text.Where(char.IsLetter).ToArray();
                await ChangeCaseAsync(letters.Length > 0 && letters.All(char.IsUpper) ? "lower" : text == TypingAssistance.ChangeCase(text, "title") ? "upper" : "title");
                return;
            }
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                CorrectBeforeParagraphBreak(id, box);
                if (string.IsNullOrEmpty(box.Text))
                {
                    var current = Document.Snapshot["content"]!["paragraphs"]!.AsArray().First(p => p!["id"]!.GetValue<string>() == id)!;
                    if (current["style"]!["list"]!.GetValue<string>() != "none")
                    {
                        e.Handled = true; await ChangeStyleAsync(style => style["list"] = "none"); box.Focus(); return;
                    }
                }
                e.Handled = true; var start = Math.Min(box.SelectionStart, box.SelectionEnd); var end = Math.Max(box.SelectionStart, box.SelectionEnd); var next = Guid.NewGuid().ToString(); await FlushAsync(); await Enqueue(() => Document.ExecuteAsync("SplitParagraph", new { id, new_id = next, start, end })); if (LastError is null) await TrackInsertAsync(next); if (LastError is null && boxes.TryGetValue(next, out var nextBox)) { active = next; nextBox.Focus(); nextBox.CaretIndex = 0; }
            }
            else if (e.Key == Key.Back && box.CaretIndex == 0 && box.SelectionStart == box.SelectionEnd)
            {
                var ps = Document.Snapshot["content"]!["paragraphs"]!.AsArray(); var index = ps.Select((p, i) => (p, i)).First(x => x.p!["id"]!.GetValue<string>() == id).i; if (index == 0) return; e.Handled = true; await FlushAsync(); var previous = ps[index - 1]!; var previousId = previous["id"]!.GetValue<string>(); var caret = previous["text"]!.GetValue<string>().Length; await Enqueue(() => Document.ExecuteAsync("MergeParagraph", new { id })); if (LastError is null) { active = previousId; boxes[previousId].Focus(); boxes[previousId].CaretIndex = caret; }
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }
    private void InsertTextAtCaret(string text)
    {
        if (active is null || !boxes.TryGetValue(active, out var box)) return;
        var start = Math.Min(box.SelectionStart, box.SelectionEnd); var end = Math.Max(box.SelectionStart, box.SelectionEnd); var current = box.Text ?? "";
        box.Text = current.Remove(start, end - start).Insert(start, text); box.CaretIndex = start + text.Length; box.Focus();
    }
    private void SetupInsertTools()
    {
        var french = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
        this.FindControl<Button>("InsertDateButton")!.Click += (_, _) => InsertTextAtCaret(DateTime.Now.ToString("d MMMM yyyy", french));
        this.FindControl<Button>("InsertTimeButton")!.Click += (_, _) => InsertTextAtCaret(DateTime.Now.ToString("HH:mm", french));
        var symbols = new MenuFlyout();
        foreach (var symbol in new[] { "©", "®", "™", "°", "±", "×", "÷", "≠", "≤", "≥", "€", "£", "§", "¶", "…", "—", "«", "»", "→", "←", "↑", "↓", "•", "✓", "★" })
        { var item = new MenuItem { Header = symbol }; item.Click += (_, _) => InsertTextAtCaret(symbol); symbols.Items.Add(item); }
        this.FindControl<Button>("InsertSymbolButton")!.Flyout = symbols;
        this.FindControl<Button>("InsertRuleButton")!.Click += async (_, _) =>
        {
            await FlushAsync(); var list = Document.Snapshot["content"]!["paragraphs"]!.AsArray(); var index = list.Select((p, i) => (p, i)).FirstOrDefault(x => x.p!["id"]!.GetValue<string>() == active).i + 1; var id = Guid.NewGuid().ToString();
            await Enqueue(() => Document.ExecuteAsync("InsertParagraph", new { id, index, text = new string('─', 40), style = new { alignment = "center", color = "#8A93A3" } }));
            active = id; boxes[id].Focus();
        };
        this.FindControl<Button>("InsertTocButton")!.Click += async (_, _) => await InsertTableOfContentsAsync();
    }
    public async Task InsertTableOfContentsAsync()
    {
        await FlushAsync(); var list = Document.Snapshot["content"]!["paragraphs"]!.AsArray();
        var headings = list.Where(p => p!["image"] is null && p["table"] is null && p["style"]!["font_size"]!.GetValue<double>() >= 22 && !string.IsNullOrWhiteSpace(p["text"]!.GetValue<string>())).ToList();
        if (headings.Count == 0) { SetStatus("Aucun titre : appliquez un style Titre pour créer un sommaire."); return; }
        var at = list.Select((p, i) => (p, i)).FirstOrDefault(x => x.p!["id"]!.GetValue<string>() == (activeImage ?? active)).i + 1;
        var commands = new List<object> { new { command = "InsertParagraph", @params = new { id = Guid.NewGuid().ToString(), index = at, text = "Sommaire", style = new { font_size = 20.0, bold = true } } } };
        foreach (var heading in headings.Take(200))
            commands.Add(new { command = "InsertParagraph", @params = new { id = Guid.NewGuid().ToString(), index = at + commands.Count, text = heading!["text"]!.GetValue<string>().Trim(), style = new { indent = heading["style"]!["font_size"]!.GetValue<double>() < 28 ? 1 : 0 } } });
        group = Guid.NewGuid().ToString();
        await Enqueue(() => Document.ExecuteAsync("Batch", new { commands = commands.ToArray() }));
    }
}
