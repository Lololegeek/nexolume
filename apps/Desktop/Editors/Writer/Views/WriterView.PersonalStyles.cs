using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System.Text.Json.Nodes;

namespace Nexolume.Desktop.Editors.Writer.Views;

public partial class WriterView
{
    private JsonObject PersonalStyles => Document.Snapshot["writer_styles"]?.AsObject() ?? new();

    public async Task SavePersonalStyleAsync(string name)
    {
        await FlushAsync();
        await Enqueue(async () =>
        {
            if (active is null || !boxes.TryGetValue(active, out var box)) throw new InvalidOperationException("Placez le curseur dans un paragraphe.");
            name = name.Trim(); var p = Paras[IndexOf(active)]!; var style = (JsonObject)p["style"]!.DeepClone();
            foreach (var property in box.StyleAt(Math.Min(box.SelectionStart, Math.Max(0, (box.Text ?? "").Length - 1))))
                if (property.Key != "link") style[property.Key] = property.Value?.DeepClone();
            style["page_break_before"] = false;
            var styles = (JsonObject)PersonalStyles.DeepClone(); styles[name] = style;
            await Document.ExecuteAsync("SetWriterStyles", new { styles });
        });
    }

    public async Task ApplyPersonalStyleAsync(string name)
    {
        if (PersonalStyles[name] is not JsonObject style) return;
        var saved = (JsonObject)style.DeepClone();
        await ChangeStyleAsync(target => { foreach (var property in saved) if (property.Key != "page_break_before") target[property.Key] = property.Value?.DeepClone(); }, true);
    }

    public async Task DeletePersonalStyleAsync(string name)
    {
        await FlushAsync(); await Enqueue(async () =>
        {
            var styles = (JsonObject)PersonalStyles.DeepClone();
            if (styles.Remove(name)) await Document.ExecuteAsync("SetWriterStyles", new { styles });
        });
    }

    private const int StyleLibraryLimit = 512_000;
    private const string StyleLibraryTooLarge = "La bibliothèque de styles dépasse 512 Ko.";

    public async Task ImportPersonalStylesAsync(string path)
    {
        await FlushAsync(); await Enqueue(async () =>
        {
            if (new FileInfo(path).Length > StyleLibraryLimit) throw new InvalidOperationException(StyleLibraryTooLarge);
            await MergePersonalStylesAsync(await File.ReadAllTextAsync(path));
        });
    }

    /// <summary>Imports a style library already read in memory (browser file picker).</summary>
    public async Task ImportPersonalStylesJsonAsync(string json)
    {
        await FlushAsync(); await Enqueue(() => MergePersonalStylesAsync(json));
    }

    private async Task MergePersonalStylesAsync(string json)
    {
        {
            var imported = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("Bibliothèque de styles invalide.");
            var styles = (JsonObject)PersonalStyles.DeepClone();
            foreach (var entry in imported)
            {
                if (styles.ContainsKey(entry.Key)) throw new InvalidOperationException($"Le style « {entry.Key} » existe déjà. Renommez-le ou supprimez-le avant l’import.");
                styles[entry.Key] = entry.Value?.DeepClone();
            }
            await Document.ExecuteAsync("SetWriterStyles", new { styles });
        }
    }

    private string PersonalStylesJson => PersonalStyles.ToJsonString(new() { WriteIndented = true });

    public async Task ExportPersonalStylesAsync(string path)
    {
        await FlushAsync(); await Enqueue(() => Platform.AppPlatform.WriteAtomicAsync(path, stream => WriteStylesAsync(stream, PersonalStylesJson)));
    }
    private static Task WriteStylesAsync(Stream stream, string json) => stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json)).AsTask();

    private async Task ShowPersonalStylesAsync()
    {
        await FlushAsync();
        var dialog = new Platform.DialogWindow { Title = "Styles personnels · Nexolume", Width = 510, Height = 570, MinWidth = 450, MinHeight = 430, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var list = new ListBox { MinHeight = 180 };
        var name = new TextBox { Watermark = "Nom du style (48 caractères maximum)", MaxLength = 48 };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#C05050") };
        var save = new Button { Content = "Enregistrer la mise en forme actuelle" };
        var apply = new Button { Content = "Appliquer", Classes = { "primary" } };
        var remove = new Button { Content = "Supprimer" };
        var import = new Button { Content = "Importer…" }; var export = new Button { Content = "Exporter…" };
        var close = new Button { Content = "Fermer" }; close.Click += (_, _) => dialog.Close();
        void Refresh(string? selected = null)
        {
            list.ItemsSource = PersonalStyles.Select(s => s.Key).Order(StringComparer.CurrentCultureIgnoreCase).ToArray(); list.SelectedItem = selected;
            apply.IsEnabled = remove.IsEnabled = list.SelectedItem is string; export.IsEnabled = PersonalStyles.Count > 0;
        }
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is string label) name.Text = label; apply.IsEnabled = remove.IsEnabled = list.SelectedItem is string; };
        save.Click += async (_, _) => { await SavePersonalStyleAsync(name.Text ?? ""); message.Text = LastError?.Message ?? "Style enregistré dans ce document. Ctrl+Z pour annuler."; Refresh(name.Text?.Trim()); };
        remove.Click += async (_, _) => { if (list.SelectedItem is string label) { await DeletePersonalStyleAsync(label); Refresh(); message.Text = LastError?.Message ?? "Style supprimé. La mise en forme du texte est conservée."; } };
        apply.Click += async (_, _) => { if (list.SelectedItem is string label) { dialog.Close(); await ApplyPersonalStyleAsync(label); } };
        import.Click += async (_, _) =>
        {
            try
            {
                if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
                var files = await storage.OpenFilePickerAsync(new() { Title = "Importer des styles", FileTypeFilter = [new("Styles Nexolume") { Patterns = ["*.json"] }] });
                if (files.FirstOrDefault() is not { } file) return;
                var bytes = await Platform.AppPlatform.ReadAllBytesAsync(file, StyleLibraryLimit, StyleLibraryTooLarge);
                await ImportPersonalStylesJsonAsync(new System.Text.UTF8Encoding(false, true).GetString(bytes)); Refresh(); message.Text = LastError?.Message ?? "Styles importés.";
            }
            catch (Exception ex) when (ex is IOException or System.Text.DecoderFallbackException) { message.Text = ex is IOException ? ex.Message : "Le fichier n’est pas en UTF-8."; }
        };
        export.Click += async (_, _) =>
        {
            try
            {
                await FlushAsync(); var json = PersonalStylesJson;
                var saved = await Platform.AppPlatform.Current.SaveFileAsync(this, new() { Title = "Exporter les styles", SuggestedFileName = "styles-nexolume.json", DefaultExtension = "json", ShowOverwritePrompt = true, FileTypeChoices = [new("Styles Nexolume") { Patterns = ["*.json"] }] }, "application/json", stream => WriteStylesAsync(stream, json));
                if (saved is not null) message.Text = "Bibliothèque exportée.";
            }
            catch (IOException ex) { message.Text = ex.Message; }
        };
        var root = new Grid { RowDefinitions = new("Auto,*,Auto,Auto,Auto,Auto"), RowSpacing = 14, Margin = new Thickness(24) };
        void Row(Control child, int row) { Grid.SetRow(child, row); root.Children.Add(child); }
        Row(new TextBlock { Text = "Conservez la mise en forme du curseur comme style. Les styles sont enregistrés dans le fichier natif et peuvent être transférés vers d’autres documents.", TextWrapping = TextWrapping.Wrap }, 0);
        Row(list, 1); Row(name, 2); Row(save, 3); Row(message, 4);
        Row(new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8, Children = { apply, remove, import, export, close } }, 5);
        dialog.Content = root; Refresh(); await dialog.ShowModalAsync(this);
    }
}
