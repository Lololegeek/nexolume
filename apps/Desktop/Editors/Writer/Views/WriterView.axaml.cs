using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using OfficeBridge.Services;
using System.Text.Json.Nodes;
namespace Nexolume.Desktop.Editors.Writer.Views;

public partial class WriterView : EditorView
{
    private readonly Dictionary<string, Controls.RichParagraphBox> boxes = [];
    private readonly Dictionary<string, string> drafts = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private bool rendering;
    private string? active;
    private DateTime lastTyping;
    private string group = Guid.NewGuid().ToString();
    public override bool HasDraft => drafts.Count > 0 || tableDrafts.Count > 0 || footnoteDrafts.Count > 0 || commentDrafts.Count > 0;
    public WriterView(DocumentSession document, WritingOptions? writing = null) : base(document)
    {
        Writing = writing ?? WritingOptions.Current;
        AvaloniaXamlLoader.Load(this);
        SetupRibbon(); SetupProductivity(); SetupAssistance(); SetupSpelling(); SetupPages(); ActualThemeVariantChanged += (_, _) => UpdateStyleButtons();
        this.FindControl<Button>("BoldButton")!.Click += async (_, _) => await StyleAsync("bold");
        this.FindControl<Button>("ItalicButton")!.Click += async (_, _) => await StyleAsync("italic");
        this.FindControl<Button>("UnderlineButton")!.Click += async (_, _) => await StyleAsync("underline");
        this.FindControl<Button>("StrikeButton")!.Click += async (_, _) => await StyleAsync("strike");
        this.FindControl<Button>("SuperscriptButton")!.Click += async (_, _) => await StyleAsync("superscript");
        this.FindControl<Button>("SubscriptButton")!.Click += async (_, _) => await StyleAsync("subscript");
        this.FindControl<Button>("AddParagraphButton")!.Click += async (_, _) => await AddParagraphAsync();
        this.FindControl<Button>("DeleteParagraphButton")!.Click += async (_, _) => { if (activeTable is not null) { await DeleteTableAsync(activeTable); return; } if (activeImage is not null) { await DeleteImageAsync(activeImage); return; } if (active is null) return; await FlushAsync(); await Enqueue(() => Document.ExecuteAsync("DeleteParagraph", new { id = active })); };
        timer.Tick += async (_, _) => { timer.Stop(); await FlushAsync(); };
        document.Changed += OnChanged; Render();
    }
    private void OnChanged(object? sender, EventArgs e) => Render();
    public override void Detach() { timer.Stop(); spellingTimer.Stop(); spellingDetached = true; Document.Changed -= OnChanged; }
    public override async Task FlushAsync() { timer.Stop(); await FlushTablesAsync(); await FlushFootnotesAsync(); await FlushCommentsAsync(); var pending = drafts.ToArray(); var pendingRuns = pending.ToDictionary(p => p.Key, p => boxes[p.Key].Runs.DeepClone()); drafts.Clear(); await TrackEditsAsync(pending); foreach (var (id, text) in pending) { await Enqueue(() => Document.ExecuteAsync("SetParagraph", new { id, text, runs = pendingRuns[id], group })); if (LastError is not null && !drafts.ContainsKey(id)) drafts[id] = text; } await Pending; DraftUpdated(); }
    private async Task StyleAsync(string property)
    {
        if (multi is not null) { await ToggleMultiAsync(property); return; }
        if (active is null) return; var id = active; var box = boxes[id]; var start = Math.Min(box.SelectionStart, box.SelectionEnd); var end = Math.Max(box.SelectionStart, box.SelectionEnd);
        var character = box.StyleAt(start); await FlushAsync(); group = Guid.NewGuid().ToString();
        if (start != end) await Enqueue(() => Document.ExecuteAsync("FormatText", new { id, start, end, style = ScriptPatch(property, !character[property]!.GetValue<bool>()) }));
        else { var p = Document.Snapshot["content"]!["paragraphs"]!.AsArray().First(p => p!["id"]!.GetValue<string>() == id)!; var style = p["style"]!.DeepClone(); var on = !style[property]!.GetValue<bool>(); style[property] = on; if (on && property == "superscript") style["subscript"] = false; if (on && property == "subscript") style["superscript"] = false; await Enqueue(() => Document.ExecuteAsync("SetParagraph", new { id, style })); }
        box.Focus(); box.CaretIndex = end; box.SelectionStart = start; box.SelectionEnd = end;
    }
    private static JsonObject ScriptPatch(string property, bool on)
    {
        var patch = new JsonObject { [property] = on };
        if (on && property == "superscript") patch["subscript"] = false;
        if (on && property == "subscript") patch["superscript"] = false;
        return patch;
    }
    public async Task AddParagraphAsync() { await FlushAsync(); var list = Document.Snapshot["content"]!["paragraphs"]!.AsArray(); var index = list.Select((p, i) => (p, i)).FirstOrDefault(x => x.p!["id"]!.GetValue<string>() == active).i + 1; var id = Guid.NewGuid().ToString(); await Enqueue(() => Document.ExecuteAsync("InsertParagraph", new { id, index, text = "" })); if (LastError is null) await TrackInsertAsync(id); active = id; boxes[id].Focus(); }
    private void Render()
    {
        rendering = true;
        try
        {
            var paragraphs = Document.Snapshot["content"]!["paragraphs"]!.AsArray(); var panel = this.FindControl<StackPanel>("ParagraphPanel")!;
            this.FindControl<Border>("WordCompatibilityNote")!.IsVisible = Document.Snapshot["office_source"]?["format"]?.GetValue<string>() == "docx";
            var ids = paragraphs.Select(p => p!["id"]!.GetValue<string>()).ToHashSet();
            foreach (var stale in boxes.Keys.Where(id => !ids.Contains(id)).ToArray()) { panel.Children.Remove((Control)boxes[stale].Parent!); boxes.Remove(stale); drafts.Remove(stale); spellingMarks.Remove(stale); spellingCache.Remove(stale); }
            foreach (var stale in tableViews.Keys.Where(id => !ids.Contains(id)).ToArray()) RemoveTableView(panel, stale);
            foreach (var stale in imageViews.Keys.Where(id => !ids.Contains(id)).ToArray()) RemoveImageView(panel, stale);
            var numbered = 0; for (int i = 0; i < paragraphs.Count; i++)
            {
                var p = paragraphs[i]!; var id = p["id"]!.GetValue<string>();
                if (p["table"] is not null) { numbered = 0; RenderTable(panel, i, p); continue; }
                if (p["image"] is not null) { numbered = 0; RenderImage(panel, i, p); continue; }
                if (!boxes.TryGetValue(id, out var box))
                {
                    box = new Controls.RichParagraphBox { Name = "Paragraph_" + i, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 18, FontFamily = new FontFamily("Georgia"), Foreground = Brushes.Black, Background = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(0, 5), MinHeight = 38, IsUndoEnabled = false };
                    box.CaretBrush = Brushes.Black; box.Resources["TextControlBorderBrushFocused"] = Brushes.Transparent; box.Resources["TextControlBorderBrushPointerOver"] = Brushes.Transparent;
                    foreach (var key in new[] { "TextControlBackgroundFocused", "TextControlBackgroundPointerOver" }) box.Resources[key] = Brushes.White;
                    foreach (var key in new[] { "TextControlForegroundFocused", "TextControlForegroundPointerOver", "TextControlForeground" }) box.Resources[key] = Brushes.Black;
                    boxes[id] = box; var wrapper = new Grid(); wrapper.Children.Add(box); wrapper.Children.Add(new Controls.UnderlineAdorner(box)); wrapper.Children.Add(new TextBlock { Foreground = Brushes.Black, IsHitTestVisible = false }); panel.Children.Insert(i, wrapper);
                    var spellingMark = new Controls.SpellingAdorner(box); ((Grid)box.Parent!).Children.Add(spellingMark); Grid.SetColumn(spellingMark, 1); spellingMarks[id] = spellingMark;
                    var paragraphGrid = (Grid)box.Parent!; paragraphGrid.RowDefinitions = new RowDefinitions("Auto,Auto");
                    foreach (var child in paragraphGrid.Children) Grid.SetRow(child, 1);
                    var breakLabel = new TextBlock { Text = "Saut de page", FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 8), IsHitTestVisible = false, IsVisible = false }; Grid.SetColumnSpan(breakLabel, 2); paragraphGrid.Children.Add(breakLabel);
                    box.TextChanged += (_, _) => ScheduleSpelling();
                    HookAssistance(id, box); HookLinks(id, box); HookParagraphKeys(id, box); SetupParagraphMenu(id, box);
                    box.GotFocus += (_, _) => { if (activeImage is not null) SelectImage(null); if (activeTable is not null) { activeTable = null; UpdateTableButtons(); } active = id; UpdateStyleButtons(); RenderPageSettings(); };
                    box.PropertyChanged += (_, e) => { if (!rendering && (e.Property == TextBox.SelectionStartProperty || e.Property == TextBox.SelectionEndProperty || e.Property == TextBox.CaretIndexProperty)) UpdateStyleButtons(); };
                    box.TextChanged += (_, _) => { if (rendering) return; var model = Document.Snapshot["content"]!["paragraphs"]!.AsArray().FirstOrDefault(p => p!["id"]!.GetValue<string>() == id); if (model is null) return; if ((box.Text ?? "") == model["text"]!.GetValue<string>()) { drafts.Remove(id); DraftUpdated(); return; } if (DateTime.UtcNow - lastTyping > TimeSpan.FromSeconds(1.5)) group = Guid.NewGuid().ToString(); lastTyping = DateTime.UtcNow; drafts[id] = box.Text ?? ""; timer.Stop(); timer.Start(); DraftUpdated(); };
                }
                var wrapperControl = (Control)box.Parent!; var currentIndex = panel.Children.IndexOf(wrapperControl); if (currentIndex != i) panel.Children.Move(currentIndex, i);
                if (!drafts.ContainsKey(id) && box.Text != p["text"]!.GetValue<string>()) box.Text = p["text"]!.GetValue<string>();
                var style = p["style"]!; if (!drafts.ContainsKey(id)) box.SetRichStyle(style, p["runs"] as JsonArray); box.FontWeight = style["bold"]!.GetValue<bool>() ? FontWeight.Bold : FontWeight.Normal; box.FontStyle = style["italic"]!.GetValue<bool>() ? FontStyle.Italic : FontStyle.Normal;
                ((Grid)box.Parent!).Children[4].IsVisible = style["page_break_before"]?.GetValue<bool>() == true;
                box.FontFamily = new FontFamily(style["font_family"]!.GetValue<string>()); box.FontSize = style["font_size"]!.GetValue<double>();
                box.TextAlignment = style["alignment"]!.GetValue<string>() switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, "justify" => TextAlignment.Justify, _ => TextAlignment.Left };
                ((Grid)box.Parent!).Children[1].IsVisible = style["underline"]!.GetValue<bool>() && box.Runs.Count == 0;
                ((Grid)box.Parent!).Children[1].InvalidateVisual();
                var foreground = Brush.Parse(style["color"]!.GetValue<string>()); var shade = style["shading"]?.GetValue<string>() ?? "none"; var frameKind = style["border"]?.GetValue<string>() ?? "none"; var background = style["highlight"]!.GetValue<string>() != "none" ? Brush.Parse(style["highlight"]!.GetValue<string>()) : shade != "none" ? Brush.Parse(shade) : Brushes.White;
                var edgeBrush = frameKind == "none" ? Brushes.Transparent : Brush.Parse("#808080"); box.BorderBrush = edgeBrush; foreach (var key in new[] { "TextControlBorderBrush", "TextControlBorderBrushFocused", "TextControlBorderBrushPointerOver" }) box.Resources[key] = edgeBrush;
                box.BorderThickness = frameKind switch { "box" => new Thickness(1), "bottom" => new Thickness(0, 0, 0, 1), "left" => new Thickness(3, 0, 0, 0), _ => new Thickness(0) }; box.Padding = frameKind is "box" or "left" ? new Thickness(8, 5) : new Thickness(0, 5);
                box.Foreground = foreground; box.Background = background; box.LineHeight = box.FontSize * style["line_spacing"]!.GetValue<double>();
                foreach (var key in new[] { "TextControlForegroundFocused", "TextControlForegroundPointerOver", "TextControlForeground" }) box.Resources[key] = foreground;
                foreach (var key in new[] { "TextControlBackgroundFocused", "TextControlBackgroundPointerOver" }) box.Resources[key] = background;
                var layoutWrapper = (Grid)box.Parent!; var list = style["list"]!.GetValue<string>(); layoutWrapper.ColumnDefinitions = new ColumnDefinitions(list == "none" ? "0,*" : "28,*"); Grid.SetColumn(box, 1); Grid.SetColumn(layoutWrapper.Children[1], 1); var marker = (TextBlock)layoutWrapper.Children[2]; numbered = list == "number" ? numbered + 1 : 0; marker.Text = list == "bullet" ? "•" : list == "number" ? numbered + "." : ""; marker.FontSize = box.FontSize; marker.Foreground = foreground; marker.Margin = new Thickness(0, 5, 0, 0); layoutWrapper.Margin = new Thickness(style["indent"]!.GetValue<int>() * 24, 0, 0, style["spacing_after"]!.GetValue<double>() * 4 / 3);
            }
            if (active is null || !boxes.ContainsKey(active)) active = paragraphs.FirstOrDefault(p => p!["image"] is null && p["table"] is null)?["id"]?.GetValue<string>();
            var words = string.Join(" ", paragraphs.Select(p => p!["text"]!.GetValue<string>() + " " + (p["table"] is { } t ? string.Join(" ", t["rows"]!.AsArray().SelectMany(row => row!.AsArray()).Select(c => c!.GetValue<string>())) : ""))).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            this.FindControl<TextBlock>("WordCount")!.Text = $"{paragraphs.Count} paragraphe{(paragraphs.Count > 1 ? "s" : "")}  ·  {words} mot{(words > 1 ? "s" : "")}";
            this.FindControl<Button>("DeleteParagraphButton")!.IsEnabled = activeImage is not null || activeTable is not null || TextParagraphCount() > 1;
            UpdateTableButtons(); UpdateStyleButtons(); RefreshProductivity(); RenderFootnotes(); RenderReview(); UpdateSearchStatus(); ScheduleSpelling(); RenderPageSettings();
        }
        finally { rendering = false; }
    }
    private JsonNode EffectiveStyle(JsonNode paragraph)
    {
        var style = paragraph["style"]!.DeepClone();
        if (active is not null && boxes.TryGetValue(active, out var box) && box.Runs.Count > 0)
            foreach (var property in box.StyleAt(Math.Min(box.SelectionStart, box.SelectionEnd))) style[property.Key] = property.Value!.DeepClone();
        return style;
    }
    private void UpdateStyleButtons() { if (active is not null && boxes.TryGetValue(active,out var selectedBox)) this.FindControl<TextBlock>("FontScopeLabel")!.Text = selectedBox.SelectionStart != selectedBox.SelectionEnd ? "Police · sélection de texte" : "Police · paragraphe actif"; var p = Document.Snapshot["content"]!["paragraphs"]!.AsArray().FirstOrDefault(p => p!["id"]!.GetValue<string>() == active); if (p is null) return; foreach (var (name, key) in new[] { ("BoldButton", "bold"), ("ItalicButton", "italic"), ("UnderlineButton", "underline"), ("StrikeButton", "strike"), ("SuperscriptButton", "superscript"), ("SubscriptButton", "subscript") }) this.FindControl<Button>(name)!.Background = EffectiveStyle(p)[key]!.GetValue<bool>() ? Brush.Parse(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light ? "#DCE8FC" : "#426497") : Brushes.Transparent; UpdateFontInputs(EffectiveStyle(p)); }
}
