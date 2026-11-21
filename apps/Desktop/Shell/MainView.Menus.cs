using Avalonia.Controls;
using Nexolume.Desktop.DesignSystem.Icons;
using Avalonia.Media;
namespace Nexolume.Desktop.Shell;

public partial class MainView
{
    private readonly List<MenuItem> documentMenuItems = [];
    private readonly List<MenuItem> writerMenuItems = [];
    private readonly List<MenuItem> sheetsMenuItems = [];
    private readonly List<MenuItem> slidesMenuItems = [];
    private void SetupFileMenu()
    {
        var menu = new MenuFlyout();
        MenuItem Item(string text, Func<Task> action, string icon, bool documentOnly = false) { var item = new MenuItem { Header = text, Icon = new OfficeIcon { Kind = icon, Stroke = Brush.Parse("#8AB3FF") } }; item.Click += (_, _) => Run(action); menu.Items.Add(item); if (documentOnly) documentMenuItems.Add(item); return item; }
        Item("Nouveau texte", () => CreateAsync("writer"), "document"); Item("Nouveau tableur", () => CreateAsync("sheets"), "sheets"); Item("Nouvelle présentation", () => CreateAsync("slides"), "slides"); menu.Items.Add(new Separator());
        Item("Ouvrir…", OpenPickerAsync, "folder"); Item("Enregistrer", () => SaveCurrentAsync(false), "save", true); Item("Enregistrer sous…", () => SaveCurrentAsync(true), "save", true);
        // Explicit formats: browsers without a native save dialog download the file and cannot offer a format list.
        foreach (var (label, format) in new[] { ("Enregistrer au format Word (.docx)…", "docx"), ("Enregistrer au format OpenDocument (.odt)…", "odt") })
        { var item = Item(label, () => SaveCurrentAsync(true, format), "save"); item.IsEnabled = false; writerMenuItems.Add(item); }
        { var item = Item("Enregistrer au format Excel (.xlsx)…", () => SaveCurrentAsync(true, "xlsx"), "save"); item.IsEnabled = false; sheetsMenuItems.Add(item); }
        { var item = Item("Enregistrer au format PowerPoint (.pptx)…", () => SaveCurrentAsync(true, "pptx"), "save"); item.IsEnabled = false; slidesMenuItems.Add(item); }
        var print = new MenuItem { Header = "Aperçu, PDF et impression…", IsEnabled = false };
        print.Click += (_, _) => Run(async () => { if (Current is Editors.Writer.Views.WriterView writer) await writer.ShowPrintPreviewAsync(); });
        menu.Items.Add(new Separator()); menu.Items.Add(print);
        // Selection changes are raised after the tab control has been initialized.
        writerMenuItems.Add(print);
        this.FindControl<Button>("FileMenuButton")!.Flyout = menu;
    }
}
