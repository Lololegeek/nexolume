using System.Text.Json.Nodes;
namespace Nexolume.Desktop.Editors.Writer.Printing;

public sealed record PageSettings(double Width = 793.7, double Height = 1122.52, double Top = 96, double Right = 96, double Bottom = 96, double Left = 96, string Header = "", string Footer = "", string HeaderAlign = "center", string FooterAlign = "center", int Columns = 1, double ColumnGap = 36)
{
    public static PageSettings Read(JsonObject snapshot) => snapshot["page_layout"] is JsonNode p
        ? new(p["width"]!.GetValue<double>(), p["height"]!.GetValue<double>(), p["top"]!.GetValue<double>(), p["right"]!.GetValue<double>(), p["bottom"]!.GetValue<double>(), p["left"]!.GetValue<double>(), p["header"]?.GetValue<string>() ?? "", p["footer"]?.GetValue<string>() ?? "", p["header_align"]?.GetValue<string>() ?? "center", p["footer_align"]?.GetValue<string>() ?? "center", p["columns"]?.GetValue<int>() ?? 1, p["column_gap"]?.GetValue<double>() ?? 36) : new();
    public object ToCommand() => new { layout = new { width = Width, height = Height, top = Top, right = Right, bottom = Bottom, left = Left, header = Header, footer = Footer, header_align = HeaderAlign, footer_align = FooterAlign, columns = Columns, column_gap = ColumnGap } };
    public void Validate()
    {
        if (Header.Length > 200 || Footer.Length > 200) throw new ArgumentException("L’en-tête et le pied de page sont limités à 200 caractères.");
        if (Columns is < 1 or > 4 || !double.IsFinite(ColumnGap) || ColumnGap is < 0 or > 200 || (Width - Left - Right - ColumnGap * (Columns - 1)) / Columns < 72) throw new ArgumentException("Les colonnes doivent faire au moins 1,9 cm de large chacune.");
        if (new[] { Width, Height, Top, Right, Bottom, Left }.Any(v => !double.IsFinite(v)) || Width is < 200 or > 2400 || Height is < 200 or > 2400 || new[] { Top, Right, Bottom, Left }.Any(v => v < 0) || Width - Left - Right < 144 || Height - Top - Bottom < 144)
            throw new ArgumentException("Les marges doivent laisser au moins 3,81 cm de texte dans chaque direction.");
    }
}
