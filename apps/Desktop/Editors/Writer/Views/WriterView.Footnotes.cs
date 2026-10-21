using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Text.Json.Nodes;
namespace Nexolume.Desktop.Editors.Writer.Views;

public partial class WriterView
{
    private readonly Dictionary<string, string> footnoteDrafts = [];
    private string footnoteSignature = "";
    private string? focusFootnote;

    private void SetupFootnotes() => this.FindControl<Button>("InsertFootnoteButton")!.Click += async (_, _) => await AddFootnoteAsync();

    /// <summary>Footnotes whose paragraph still exists, in reading order.</summary>
    private List<JsonObject> LiveFootnotes()
    {
        var order = Document.Snapshot["content"]!["paragraphs"]!.AsArray().Select((p, i) => (Id: p!["id"]!.GetValue<string>(), i)).ToDictionary(x => x.Id, x => x.i);
        return (Document.Snapshot["footnotes"] as JsonArray ?? []).OfType<JsonObject>().Where(n => order.ContainsKey(n["paragraph"]!.GetValue<string>())).OrderBy(n => order[n["paragraph"]!.GetValue<string>()]).ToList();
    }

    private async Task SetFootnotesAsync(IEnumerable<JsonObject> notes)
    {
        var list = new JsonArray(notes.Select(n => (JsonNode)n.DeepClone()).ToArray());
        await Enqueue(() => Document.ExecuteAsync("SetFootnotes", new { footnotes = list }));
    }

    public async Task AddFootnoteAsync(string text = "")
    {
        await FlushAsync(); if (active is null || !boxes.ContainsKey(active)) return;
        var id = Guid.NewGuid().ToString("N")[..12]; focusFootnote = id;
        var notes = LiveFootnotes(); notes.Add(new JsonObject { ["id"] = id, ["paragraph"] = active, ["text"] = text });
        await SetFootnotesAsync(notes);
    }

    private async Task FlushFootnotesAsync()
    {
        if (footnoteDrafts.Count == 0) return;
        var pending = footnoteDrafts.ToArray(); footnoteDrafts.Clear();
        var notes = LiveFootnotes(); foreach (var (id, text) in pending) if (notes.FirstOrDefault(n => n["id"]!.GetValue<string>() == id) is { } note) note["text"] = text;
        await SetFootnotesAsync(notes);
    }

    private void RenderFootnotes()
    {
        var notes = LiveFootnotes(); var paragraphs = Document.Snapshot["content"]!["paragraphs"]!.AsArray();
        var signature = string.Join("\u0001", notes.Select(n => n["id"] + "\u0002" + n["paragraph"] + "\u0002" + n["text"]));
        if (signature == footnoteSignature) return; footnoteSignature = signature;
        var host = this.FindControl<Border>("FootnotePanel")!; var list = this.FindControl<StackPanel>("FootnoteList")!; list.Children.Clear(); host.IsVisible = notes.Count > 0;
        TextBox? focus = null;
        for (int i = 0; i < notes.Count; i++)
        {
            var note = notes[i]; var id = note["id"]!.GetValue<string>(); var anchor = paragraphs.FirstOrDefault(p => p!["id"]!.GetValue<string>() == note["paragraph"]!.GetValue<string>())?["text"]?.GetValue<string>() ?? "";
            anchor = anchor.Length > 36 ? anchor[..36] + "…" : anchor; if (anchor.Length == 0) anchor = "paragraphe sans texte";
            var box = new TextBox { Name = "Footnote_" + i, Text = note["text"]!.GetValue<string>(), Watermark = "Texte de la note", AcceptsReturn = false, MaxLength = 5000, FontSize = 12, Foreground = Brushes.Black, Background = Brushes.Transparent, BorderThickness = new Thickness(0), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 2) };
            foreach (var key in new[] { "TextControlBackgroundFocused", "TextControlBackgroundPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushPointerOver" }) box.Resources[key] = Brushes.Transparent;
            foreach (var key in new[] { "TextControlForegroundFocused", "TextControlForegroundPointerOver", "TextControlForeground" }) box.Resources[key] = Brushes.Black;
            box.TextChanged += (_, _) => { if (!rendering) { footnoteDrafts[id] = box.Text ?? ""; DraftUpdated(); } };
            box.LostFocus += async (_, _) => { await FlushFootnotesAsync(); DraftUpdated(); };
            box.KeyDown += async (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; await FlushFootnotesAsync(); } };
            var remove = new Button { Name = "RemoveFootnote_" + i, Content = "×", Classes = { "toolText" }, Padding = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(remove, "Supprimer la note"); remove.Click += async (_, _) => { footnoteDrafts.Remove(id); await FlushFootnotesAsync(); await SetFootnotesAsync(LiveFootnotes().Where(n => n["id"]!.GetValue<string>() != id)); };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*,Auto") };
            row.Children.Add(new TextBlock { Text = (i + 1).ToString(), FontSize = 10, Foreground = Brushes.Black, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 3, 0, 0) });
            var body = new StackPanel { Spacing = 0 }; body.Children.Add(box); body.Children.Add(new TextBlock { Text = "À la fin de « " + anchor + " »", FontSize = 10, Foreground = Brushes.Gray, Margin = new Thickness(4, 0, 0, 0) });
            Grid.SetColumn(body, 1); Grid.SetColumn(remove, 2); row.Children.Add(body); row.Children.Add(remove); list.Children.Add(row);
            if (id == focusFootnote) focus = box;
        }
        list.Children.Insert(0, new TextBlock { Text = "Notes de bas de page", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 2) });
        if (focus is not null) { focusFootnote = null; Avalonia.Threading.Dispatcher.UIThread.Post(() => focus.Focus(), Avalonia.Threading.DispatcherPriority.Input); }
    }
}
