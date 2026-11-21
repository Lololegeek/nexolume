using Avalonia.Controls;
using Avalonia.Media;
using Nexolume.Desktop.DesignSystem.Controls;
using Nexolume.Desktop.Platform;
using Nexolume.Desktop.Services;

namespace Nexolume.Desktop.Editors.Writer.Views;

public partial class WriterView
{
    private static readonly string[] RecommendedFonts =
    [
        "Aptos", "Calibri", "Cambria", "Candara", "Constantia", "Corbel", "Segoe UI", "Arial", "Arial Narrow", "Verdana", "Tahoma", "Trebuchet MS",
        "Georgia", "Times New Roman", "Palatino Linotype", "Book Antiqua", "Garamond", "Bookman Old Style", "Century Gothic", "Franklin Gothic Medium",
        "Gill Sans MT", "Consolas", "Courier New", "Lucida Console", "Cascadia Code", "Comic Sans MS", "Impact", "Segoe Print", "Segoe Script", "Ink Free"
    ];
    private List<string> installedFonts = [];

    private void SetupFonts()
    {
        installedFonts = InstalledFonts();
        var picker = this.FindControl<FontPicker>("FontFamilyBox")!;
        picker.CatalogProvider = FontCatalog;
        picker.Favorites = () => FontPreferences.Current.Favorites;
        picker.SetFavorite = FontPreferences.Current.SetFavorite;
        AttachedToVisualTree += (_, _) => DeviceFonts.Changed += OnDeviceFontsChanged;
        DetachedFromVisualTree += (_, _) => DeviceFonts.Changed -= OnDeviceFontsChanged;
    }

    private static List<string> InstalledFonts()
    {
        IEnumerable<string> names = AppPlatform.IsBrowser ? DeviceFonts.Families.Concat(DeviceFonts.Bundled) : FontManager.Current.SystemFonts.Select(f => f.Name);
        return names.Where(n => !string.IsNullOrWhiteSpace(n) && !n.StartsWith('@')).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // Device fonts were listed or a family's files were read (browser): refresh the picker and lay the text out again.
    private void OnDeviceFontsChanged()
    {
        installedFonts = InstalledFonts();
        this.FindControl<FontPicker>("FontFamilyBox")!.RefreshFaces();
        foreach (var paragraph in boxes.Values) { var family = paragraph.FontFamily; paragraph.FontFamily = FontFamily.Default; paragraph.FontFamily = family; paragraph.InvalidateVisual(); }
    }

    private FontPicker.Catalog FontCatalog() => new()
    {
        Recent = FontPreferences.Current.Recent,
        Recommended = RecommendedFonts.Where(n => n == "Aptos" || installedFonts.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList(),
        All = installedFonts,
        AllTitle = AppPlatform.IsBrowser ? (DeviceFonts.Families.Count > 0 ? L.T("Polices de l’appareil") : L.T("Polices incluses")) : L.T("Toutes les polices"),
        ActionText = DeviceFonts.CanRequest ? L.T("Afficher les polices de cet appareil") : null,
        Action = DeviceFonts.CanRequest ? RequestDeviceFontsAsync : null,
        Note = AppPlatform.IsBrowser && DeviceFonts.Families.Count == 0 && !DeviceFonts.CanRequest ? L.T("Polices de l’appareil : Chrome ou Edge requis") : null,
    };

    private async Task RequestDeviceFontsAsync()
    {
        SetStatus("Autorisez l’accès aux polices dans la fenêtre du navigateur…");
        SetStatus(await DeviceFonts.RequestAsync() ? $"{DeviceFonts.Families.Count} polices de l’appareil disponibles." : "Le navigateur n’a pas donné accès aux polices de l’appareil.");
    }

    private static void RememberFont(string font) => FontPreferences.Current.Remember(font);
}
