using Avalonia.Controls;
using Nexolume.Desktop.DesignSystem.Icons;
using Avalonia.Media;
namespace Nexolume.Desktop.Shell;

public partial class MainWindow
{
    private readonly List<MenuItem> documentMenuItems = [];
    private void SetupFileMenu()
    {
        var menu = new MenuFlyout();
        void Item(string text, Func<Task> action, string icon, bool documentOnly = false) { var item = new MenuItem { Header = text, Icon = new OfficeIcon { Kind = icon, Stroke = Brush.Parse("#8AB3FF") } }; item.Click += (_, _) => Run(action); menu.Items.Add(item); if (documentOnly) documentMenuItems.Add(item); }
        Item("Nouveau texte", () => CreateAsync("writer"), "document"); Item("Nouveau tableur", () => CreateAsync("sheets"), "sheets"); Item("Nouvelle présentation", () => CreateAsync("slides"), "slides"); menu.Items.Add(new Separator());
        Item("Ouvrir…", OpenPickerAsync, "folder"); Item("Enregistrer", () => SaveCurrentAsync(false), "save", true); Item("Enregistrer sous…", () => SaveCurrentAsync(true), "save", true);
        var print = new MenuItem { Header = "Aperçu, PDF et impression…", IsEnabled = false };
        print.Click += (_, _) => Run(async () => { if (Current is Editors.Writer.Views.WriterView writer) await writer.ShowPrintPreviewAsync(); });
        menu.Items.Add(new Separator()); menu.Items.Add(print);
        // Selection changes are raised after the tab control has been initialized.
        writerMenuItems.Add(print);
        this.FindControl<Button>("FileMenuButton")!.Flyout = menu;
    }
    private readonly List<MenuItem> writerMenuItems = [];
}
