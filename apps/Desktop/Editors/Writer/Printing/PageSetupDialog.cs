using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Nexolume.Desktop.Editors.Writer.Printing;

public sealed class PageSetupDialog : Window
{
    private readonly ComboBox paper = new() { Name = "PaperSize", ItemsSource = new[] { "A4 · 21 × 29,7 cm", "A5 · 14,8 × 21 cm", "Lettre · 21,59 × 27,94 cm", "Personnalisé" }, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox orientation = new() { Name = "PageOrientation", ItemsSource = new[] { "Portrait", "Paysage" }, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown width = Number("PaperWidth", 5.3m, 63.5m), height = Number("PaperHeight", 5.3m, 63.5m);
    private readonly NumericUpDown top = Number("MarginTop", 0, 30), right = Number("MarginRight", 0, 30), bottom = Number("MarginBottom", 0, 30), left = Number("MarginLeft", 0, 30);
    private readonly TextBlock error = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = Avalonia.Media.Brush.Parse("#DC6B6B"), Name = "PageSetupError" };
    private readonly TextBox headerText = new() { Name = "PageHeaderText", Watermark = "Texte répété en haut de chaque page" }, footerText = new() { Name = "PageFooterText", Watermark = "Par exemple : Page {page} sur {pages}" };
    private readonly ComboBox headerAlign = new() { Name = "PageHeaderAlign", ItemsSource = new[] { "À gauche", "Centré", "À droite" }, HorizontalAlignment = HorizontalAlignment.Stretch }, footerAlign = new() { Name = "PageFooterAlign", ItemsSource = new[] { "À gauche", "Centré", "À droite" }, HorizontalAlignment = HorizontalAlignment.Stretch };
    private static readonly string[] Aligns = ["left", "center", "right"];
    private readonly ComboBox columns = new() { Name = "PageColumns", ItemsSource = new[] { "1 colonne", "2 colonnes", "3 colonnes", "4 colonnes" }, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown columnGap = Number("PageColumnGap", 0, 5);
    private bool changing;
    public PageSetupDialog(PageSettings settings)
    {
        Title = "Mise en page · Nexolume"; Width = 490; Height = 880; CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var fields = new Grid { ColumnDefinitions = new("*,*"), RowDefinitions = new("Auto,Auto,Auto,Auto"), ColumnSpacing = 20, RowSpacing = 16 };
        void Field(string label, Control control, int row, int column) { var stack = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = label }, control } }; Grid.SetRow(stack, row); Grid.SetColumn(stack, column); fields.Children.Add(stack); }
        Field("Largeur (cm)", width, 0, 0); Field("Hauteur (cm)", height, 0, 1); Field("Haut (cm)", top, 1, 0); Field("Bas (cm)", bottom, 1, 1); Field("Gauche (cm)", left, 2, 0); Field("Droite (cm)", right, 2, 1);
        var apply = new Button { Name = "ApplyPageSetup", Content = "Appliquer", HorizontalAlignment = HorizontalAlignment.Right }; apply.Classes.Add("primary");
        var cancel = new Button { Name = "CancelPageSetup", Content = "Annuler" }; cancel.Click += (_, _) => Close();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, apply } };
        Content = new StackPanel { Margin = new Thickness(26), Spacing = 16, Children = { new TextBlock { Text = "Papier et marges", FontSize = 22, FontWeight = Avalonia.Media.FontWeight.SemiBold }, paper, orientation, fields, new TextBlock { Text = "Colonnes", FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) }, Row(columns, columnGap), new TextBlock { Text = "En-tête et pied de page", FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) }, Row(headerText, headerAlign), Row(footerText, footerAlign), new TextBlock { Text = "{page} = numéro de la page, {pages} = nombre de pages.", Opacity = .7, FontSize = 12 }, error, actions } };
        var scope = new NameScope(); NameScope.SetNameScope(this, scope); foreach (var control in new Control[] { headerText, footerText, headerAlign, footerAlign, paper, orientation, columns, columnGap, width, height, top, right, bottom, left, error, apply, cancel }) scope.Register(control.Name!, control);
        columns.SelectedIndex = Math.Clamp(settings.Columns, 1, 4) - 1; columnGap.Value = Cm(settings.ColumnGap); headerText.Text = settings.Header; footerText.Text = settings.Footer; headerAlign.SelectedIndex = Math.Max(0, Array.IndexOf(Aligns, settings.HeaderAlign)); footerAlign.SelectedIndex = Math.Max(0, Array.IndexOf(Aligns, settings.FooterAlign));
        changing = true; width.Value = Cm(settings.Width); height.Value = Cm(settings.Height); top.Value = Cm(settings.Top); right.Value = Cm(settings.Right); bottom.Value = Cm(settings.Bottom); left.Value = Cm(settings.Left); orientation.SelectedIndex = settings.Width > settings.Height ? 1 : 0;
        var small = Math.Min(settings.Width, settings.Height); var big = Math.Max(settings.Width, settings.Height);
        paper.SelectedIndex = Math.Abs(small - 793.7) < 1 && Math.Abs(big - 1122.52) < 1 ? 0 : Math.Abs(small - 559.37) < 1 && Math.Abs(big - 793.7) < 1 ? 1 : Math.Abs(small - 816) < 1 && Math.Abs(big - 1056) < 1 ? 2 : 3; changing = false;
        paper.SelectionChanged += (_, _) => { if (changing || paper.SelectedIndex == 3) return; var size = paper.SelectedIndex switch { 1 => (14.8m,21m), 2 => (21.59m,27.94m), _ => (21m,29.7m) }; changing = true; width.Value = orientation.SelectedIndex == 1 ? size.Item2 : size.Item1; height.Value = orientation.SelectedIndex == 1 ? size.Item1 : size.Item2; changing = false; };
        orientation.SelectionChanged += (_, _) => { if (changing) return; changing = true; (width.Value,height.Value) = (height.Value,width.Value); changing = false; };
        foreach (var number in new[] { width, height }) number.ValueChanged += (_, _) => { if (!changing) { changing = true; paper.SelectedIndex = 3; orientation.SelectedIndex = width.Value > height.Value ? 1 : 0; changing = false; } };
        apply.Click += (_, _) => { try { var value = new PageSettings(Px(width), Px(height), Px(top), Px(right), Px(bottom), Px(left), headerText.Text ?? "", footerText.Text ?? "", Aligns[Math.Max(0, headerAlign.SelectedIndex)], Aligns[Math.Max(0, footerAlign.SelectedIndex)], Math.Max(0, columns.SelectedIndex) + 1, Px(columnGap)); value.Validate(); Close(value); } catch (ArgumentException ex) { error.Text = ex.Message; } };
    }
    private static Grid Row(Control text, Control align) { var grid = new Grid { ColumnDefinitions = new("*,130"), ColumnSpacing = 12 }; Grid.SetColumn(align, 1); grid.Children.Add(text); grid.Children.Add(align); return grid; }
    private static decimal Cm(double value) => Math.Round((decimal)(value * 2.54 / 96), 3);
    private static double Px(NumericUpDown control) => (double)(control.Value ?? throw new ArgumentException("Renseignez toutes les dimensions.")) * 96 / 2.54;
    private static NumericUpDown Number(string name, decimal min, decimal max) => new() { Name = name, Minimum = min, Maximum = max, Increment = .1m, FormatString = "0.###", HorizontalAlignment = HorizontalAlignment.Stretch };
}
