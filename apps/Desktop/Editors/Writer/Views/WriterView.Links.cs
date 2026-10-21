using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Nexolume.Desktop.Editors.Writer.Controls;

namespace Nexolume.Desktop.Editors.Writer.Views;

public partial class WriterView
{
    private const string LinkColor = "#0563C1";
    private TextBox linkAddress = null!, linkLabel = null!;
    private TextBlock linkTitle = null!;
    private Button linkApply = null!, linkRemove = null!, linkOpen = null!;
    private Flyout linkFlyout = null!;
    // Where the link dialog applies: a single-paragraph range (an empty range inserts new text) or a multi-paragraph selection.
    private (string Id, int Start, int End, bool Existing, (int First, int FirstOffset, int Last, int LastOffset)? Multi) linkTarget;

    private void SetupLinks()
    {
        linkAddress = new TextBox { Name = "LinkAddressBox", Watermark = "https://exemple.fr ou nom@exemple.fr", MaxLength = 2000, MinWidth = 340 };
        linkLabel = new TextBox { Name = "LinkTextBox", Watermark = "Texte à afficher", MaxLength = 500 };
        linkTitle = new TextBlock { Text = "Insérer un lien", FontSize = 17, FontWeight = Avalonia.Media.FontWeight.SemiBold };
        linkApply = new Button { Name = "LinkApplyButton", Content = "Appliquer", IsEnabled = false };
        linkRemove = new Button { Name = "LinkRemoveButton", Content = "Supprimer le lien" };
        linkOpen = new Button { Name = "LinkOpenButton", Content = "Ouvrir" };
        var panel = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(6), Width = 360 };
        panel.Children.Add(linkTitle);
        panel.Children.Add(new TextBlock { Text = "Adresse", Classes = { "caption" } }); panel.Children.Add(linkAddress);
        panel.Children.Add(new TextBlock { Text = "Texte à afficher", Classes = { "caption" } }); panel.Children.Add(linkLabel);
        panel.Children.Add(new TextBlock { Text = "Ctrl+clic sur un lien pour l’ouvrir. Adresses http, https et mailto.", Classes = { "caption" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { linkApply, linkRemove, linkOpen } });
        linkFlyout = new Flyout { Content = panel };
        var insert = this.FindControl<Button>("InsertLinkButton")!;
        insert.Flyout = linkFlyout;
        linkFlyout.Opening += (_, _) => PrepareLink();
        void Validate() => linkApply.IsEnabled = NormalizeLink(linkAddress.Text) is not null;
        linkAddress.TextChanged += (_, _) => { Validate(); linkOpen.IsEnabled = linkApply.IsEnabled; };
        linkApply.Click += async (_, _) => { linkFlyout.Hide(); await ApplyLinkAsync(); };
        linkAddress.KeyDown += (_, e) => { if (e.Key == Key.Enter && linkApply.IsEnabled) { e.Handled = true; linkFlyout.Hide(); _ = ApplyLinkAsync(); } };
        linkRemove.Click += async (_, _) => { linkFlyout.Hide(); await RemoveLinkAsync(); };
        linkOpen.Click += (_, _) => OpenLink(NormalizeLink(linkAddress.Text));
        this.FindControl<Button>("RemoveLinkButton")!.Click += async (_, _) => { PrepareLink(); await RemoveLinkAsync(); };
    }

    public void ShowLinkFlyout() { var button = this.FindControl<Button>("InsertLinkButton")!; linkFlyout.ShowAt(button); }

    // Accepts what people type (a bare domain or an e-mail) and returns an address the engine accepts, or null.
    public static string? NormalizeLink(string? raw)
    {
        var text = (raw ?? "").Trim(); if (text.Length == 0 || text.Length > 2000 || text.Any(char.IsWhiteSpace) || text.Any(char.IsControl)) return null;
        var lower = text.ToLowerInvariant();
        if (lower.StartsWith("http://") || lower.StartsWith("https://")) return text.Length > lower.IndexOf("//") + 2 ? text : null;
        if (lower.StartsWith("mailto:")) return text.Length > 7 ? text : null;
        if (text.Contains(':') && !text.Contains('@')) return null;
        if (text.Contains('@') && !text.StartsWith('@') && !text.EndsWith('@')) return "mailto:" + text;
        return text.Contains('.') && !text.StartsWith('.') && !text.EndsWith('.') ? "https://" + text : null;
    }

    // The contiguous run of text sharing the link found at (or just before) the offset.
    private static (int Start, int End, string Link)? LinkRangeAt(RichParagraphBox box, int offset)
    {
        var runs = box.Runs.Select(r => (Start: r!["start"]!.GetValue<int>(), Length: r["length"]!.GetValue<int>(), Link: r["style"]?["link"]?.GetValue<string>() ?? "")).ToList();
        foreach (var candidate in new[] { offset, offset - 1 })
        {
            if (candidate < 0) continue;
            var hit = runs.FindIndex(r => r.Link.Length > 0 && candidate >= r.Start && candidate < r.Start + r.Length);
            if (hit < 0) continue;
            int first = hit, last = hit;
            while (first > 0 && runs[first - 1].Link == runs[hit].Link && runs[first - 1].Start + runs[first - 1].Length == runs[first].Start) first--;
            while (last < runs.Count - 1 && runs[last + 1].Link == runs[hit].Link && runs[last].Start + runs[last].Length == runs[last + 1].Start) last++;
            return (runs[first].Start, runs[last].Start + runs[last].Length, runs[hit].Link);
        }
        return null;
    }

    private void PrepareLink()
    {
        string address = "", label = ""; var existing = false;
        if (multi is not null && MultiRange() is { } range)
        {
            var firstId = IdAt(range.First); var first = boxes[firstId];
            address = first.StyleAt(Math.Min(range.FirstOffset, TextOf(firstId).Length))["link"]?.GetValue<string>() ?? "";
            label = SelectedText(); linkTarget = (firstId, range.FirstOffset, range.FirstOffset, address.Length > 0, range);
        }
        else if (active is not null && boxes.TryGetValue(active, out var rich))
        {
            var start = Math.Min(rich.SelectionStart, rich.SelectionEnd); var end = Math.Max(rich.SelectionStart, rich.SelectionEnd); var text = rich.Text ?? "";
            if (start != end) { label = text[start..end]; address = rich.StyleAt(start)["link"]?.GetValue<string>() ?? ""; existing = address.Length > 0; }
            else if (LinkRangeAt(rich, start) is { } found) { start = found.Start; end = found.End; label = text[start..end]; address = found.Link; existing = true; }
            linkTarget = (active, start, end, existing, null);
        }
        else linkTarget = ("", 0, 0, false, null);
        linkAddress.Text = address; linkLabel.Text = label.Replace('\n', ' ');
        linkLabel.IsEnabled = linkTarget.Multi is null && linkTarget.End == linkTarget.Start;
        linkTitle.Text = existing ? "Modifier le lien" : "Insérer un lien";
        linkRemove.IsEnabled = existing || linkTarget.Multi is not null;
        linkApply.IsEnabled = linkOpen.IsEnabled = NormalizeLink(address) is not null;
    }

    // Programmatic entry points used by the dialog and by tests: they target the current selection or caret.
    public async Task SetLinkAsync(string address, string? label = null) { PrepareLink(); linkAddress.Text = address; if (label is not null) linkLabel.Text = label; await ApplyLinkAsync(); }
    public async Task ClearLinkAsync() { PrepareLink(); await RemoveLinkAsync(); }

    public async Task ApplyLinkAsync()
    {
        var url = NormalizeLink(linkAddress.Text); if (url is null) { SetStatus("Adresse de lien invalide."); return; }
        var patch = new JsonObject { ["link"] = url, ["underline"] = true, ["color"] = LinkColor };
        await FlushAsync();
        if (linkTarget.Multi is { } r) { await FormatPiecesAsync(r, patch); SetStatus("Lien appliqué."); return; }
        var (id, start, end, _, _) = linkTarget; if (!boxes.TryGetValue(id, out var box)) return;
        group = Guid.NewGuid().ToString();
        if (end > start) await Enqueue(() => Document.ExecuteAsync("FormatText", new { id, start, end, style = patch }));
        else
        {
            var text = string.IsNullOrWhiteSpace(linkLabel.Text) ? url : linkLabel.Text!.Trim(); end = start + text.Length;
            await Enqueue(() => Document.ExecuteAsync("Batch", new { commands = new object[]
            {
                new { command = "ReplaceText", @params = new { id, start, end = start, text } },
                new { command = "FormatText", @params = new { id, start, end, style = patch } }
            }}));
        }
        box.Focus(); box.CaretIndex = end;
        SetStatus(LastError is null ? "Lien appliqué. Ctrl+clic pour l’ouvrir." : "Lien refusé : " + LastError.Message);
    }

    public async Task RemoveLinkAsync()
    {
        await FlushAsync();
        JsonObject Plain(JsonNode paragraph) => new() { ["link"] = "", ["underline"] = paragraph["style"]!["underline"]!.GetValue<bool>(), ["color"] = paragraph["style"]!["color"]!.GetValue<string>() };
        if (linkTarget.Multi is { } r) { await FormatPiecesAsync(r, Plain(Paras[r.First]!)); SetStatus("Lien supprimé."); return; }
        var (id, start, end, existing, _) = linkTarget;
        if (!existing || end <= start || !boxes.TryGetValue(id, out var box)) { SetStatus("Placez le curseur dans un lien pour le supprimer."); return; }
        var style = Plain(Paras[IndexOf(id)]!);
        group = Guid.NewGuid().ToString();
        await Enqueue(() => Document.ExecuteAsync("FormatText", new { id, start, end, style }));
        box.Focus(); box.CaretIndex = end; SetStatus(LastError is null ? "Lien supprimé." : "Échec : " + LastError.Message);
    }

    private void OpenLink(string? url)
    {
        if (url is null || !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); SetStatus("Ouverture de " + url); }
        catch (Exception error) { SetStatus("Impossible d’ouvrir le lien : " + error.Message); }
    }

    // Ctrl+click opens the link under the caret.
    private void HookLinks(string id, TextBox box)
    {
        if (box is not RichParagraphBox rich) return;
        rich.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || rich.SelectionStart != rich.SelectionEnd) return;
            if (LinkRangeAt(rich, rich.CaretIndex) is { } link) { e.Handled = true; OpenLink(link.Link); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }
}
