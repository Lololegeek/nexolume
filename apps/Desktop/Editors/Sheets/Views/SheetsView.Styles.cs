using Avalonia.Controls;
using Avalonia.Media;
using System.Text.Json.Nodes;
namespace Nexolume.Desktop.Editors.Sheets.Views;

public partial class SheetsView
{
    private static readonly (string Label, string Value)[] TextColors = [("Texte auto", "none"), ("Noir", "#000000"), ("Rouge", "#C00000"), ("Orange", "#ED7D31"), ("Vert", "#2E7D32"), ("Bleu", "#1F4E9E"), ("Violet", "#7030A0"), ("Gris", "#7F7F7F")];
    private static readonly (string Label, string Value)[] FillColors = [("Sans remplissage", "none"), ("Jaune", "#FFF2A8"), ("Vert clair", "#D5EFD0"), ("Bleu clair", "#D6E6F7"), ("Rose", "#F8D7E3"), ("Orange clair", "#FBE0C3"), ("Gris clair", "#E7E9EA"), ("Rouge clair", "#F7C9C9")];

    private static readonly (string Label, string Formula)[] FunctionCatalog =
    [
        ("Somme · SOMME", "=SUM({r})"), ("Moyenne · MOYENNE", "=AVERAGE({r})"), ("Minimum · MIN", "=MIN({r})"), ("Maximum · MAX", "=MAX({r})"),
        ("Nombre de valeurs · NB", "=COUNT({r})"), ("Cellules non vides · NBVAL", "=COUNTA({r})"), ("Cellules vides · NB.VIDE", "=COUNTBLANK({r})"),
        ("Médiane · MEDIANE", "=MEDIAN({r})"), ("Produit · PRODUIT", "=PRODUCT({r})"), ("Plus grande valeur · GRANDE.VALEUR", "=LARGE({r},1)"), ("Plus petite valeur · PETITE.VALEUR", "=SMALL({r},1)"),
        ("Condition · SI", "=IF({c}>0,\"Oui\",\"Non\")"), ("Si erreur · SIERREUR", "=IFERROR({c},0)"), ("Et · ET", "=AND({c}>0,{c}<100)"), ("Ou · OU", "=OR({c}>0,{c}<0)"),
        ("Compter si · NB.SI", "=COUNTIF({r},\">0\")"), ("Somme si · SOMME.SI", "=SUMIF({r},\">0\")"), ("Moyenne si · MOYENNE.SI", "=AVERAGEIF({r},\">0\")"),
        ("Recherche verticale · RECHERCHEV", "=VLOOKUP({c},{r},2,FALSE)"), ("Équivalence · EQUIV", "=MATCH({c},{r},0)"), ("Index · INDEX", "=INDEX({r},1)"),
        ("Arrondi · ARRONDI", "=ROUND({c},2)"), ("Arrondi supérieur · ARRONDI.SUP", "=ROUNDUP({c},0)"), ("Arrondi inférieur · ARRONDI.INF", "=ROUNDDOWN({c},0)"),
        ("Valeur absolue · ABS", "=ABS({c})"), ("Racine carrée · RACINE", "=SQRT({c})"), ("Puissance · PUISSANCE", "=POWER({c},2)"), ("Modulo · MOD", "=MOD({c},2)"), ("Partie entière · ENT", "=INT({c})"), ("Pi · PI", "=PI()"),
        ("Concaténer · CONCAT", "=CONCAT({c},\" \",{c})"), ("Longueur · NBCAR", "=LEN({c})"), ("Majuscules · MAJUSCULE", "=UPPER({c})"), ("Minuscules · MINUSCULE", "=LOWER({c})"),
        ("Supprimer les espaces · SUPPRESPACE", "=TRIM({c})"), ("Gauche · GAUCHE", "=LEFT({c},3)"), ("Droite · DROITE", "=RIGHT({c},3)"), ("Extraire · STXT", "=MID({c},2,3)"), ("Texte en nombre · CNUM", "=VALUE({c})"),
        ("Est un nombre · ESTNUM", "=ISNUMBER({c})"), ("Est vide · ESTVIDE", "=ISBLANK({c})"), ("Est une erreur · ESTERREUR", "=ISERROR({c})"),
        ("Somme multi-critères · SOMME.SI.ENS", "=SUMIFS({r},{r},\">0\")"), ("Compter multi-critères · NB.SI.ENS", "=COUNTIFS({r},\">0\")"), ("Moyenne multi-critères · MOYENNE.SI.ENS", "=AVERAGEIFS({r},{r},\">0\")"),
        ("Conditions multiples · SI.CONDITIONS", "=IFS({c}>100,\"Haut\",{c}>0,\"Moyen\")"), ("Si non disponible · SI.NON.DISP", "=IFNA({c},0)"), ("Choisir · CHOISIR", "=CHOOSE(1,{c},{c})"), ("Ou exclusif · OUX", "=XOR({c}>0,{c}<10)"),
        ("Recherche horizontale · RECHERCHEH", "=HLOOKUP({c},{r},2,FALSE)"), ("Somme des produits · SOMMEPROD", "=SUMPRODUCT({r},{r})"), ("Rang · RANG", "=RANK({c},{r})"),
        ("Écart type · ECARTYPE", "=STDEV({r})"), ("Écart type population · ECARTYPE.P", "=STDEVP({r})"), ("Variance · VAR", "=VAR({r})"), ("Variance population · VAR.P", "=VARP({r})"),
        ("Plafond · PLAFOND", "=CEILING({c},5)"), ("Plancher · PLANCHER", "=FLOOR({c},5)"), ("Factorielle · FACT", "=FACT(5)"), ("Combinaisons · COMBIN", "=COMBIN(5,2)"), ("PGCD · PGCD", "=GCD({c},12)"), ("PPCM · PPCM", "=LCM({c},12)"),
        ("Nombre aléatoire · ALEA", "=RAND()"), ("Entier aléatoire · ALEA.ENTRE.BORNES", "=RANDBETWEEN(1,100)"), ("Est pair · EST.PAIR", "=ISEVEN({c})"), ("Est impair · EST.IMPAIR", "=ISODD({c})"),
        ("Sinus · SIN", "=SIN({c})"), ("Cosinus · COS", "=COS({c})"), ("Tangente · TAN", "=TAN({c})"), ("Degrés · DEGRES", "=DEGREES({c})"), ("Radians · RADIANS", "=RADIANS({c})"),
        ("Trouver · TROUVE", "=FIND(\"a\",{c})"), ("Chercher · CHERCHE", "=SEARCH(\"a\",{c})"), ("Substituer · SUBSTITUE", "=SUBSTITUTE({c},\"a\",\"b\")"), ("Répéter · REPT", "=REPT({c},3)"),
        ("Identique · EXACT", "=EXACT({c},{c})"), ("Nom propre · NOMPROPRE", "=PROPER({c})"), ("Joindre du texte · JOINDRE.TEXTE", "=TEXTJOIN(\", \",TRUE,{r})"),
        ("Date · DATE", "=DATE(2025,1,1)"), ("Aujourd'hui · AUJOURDHUI", "=TODAY()"), ("Maintenant · MAINTENANT", "=NOW()"), ("Année · ANNEE", "=YEAR({c})"), ("Mois · MOIS", "=MONTH({c})"), ("Jour · JOUR", "=DAY({c})"), ("Jour de la semaine · JOURSEM", "=WEEKDAY({c},2)"),
    ];

    private void SetupRichStyles()
    {
        void Toggle(string name, string key) => this.FindControl<Button>(name)!.Click += async (_, _) => { await FlushAsync(); await FormatSelectionAsync(key, JsonValue.Create(!StyleFlag(CellStyle(), key))!); };
        Toggle("CellItalicButton", "italic"); Toggle("CellUnderlineButton", "underline");
        void Align(string name, string value) => this.FindControl<Button>(name)!.Click += async (_, _) => { await FlushAsync(); var now = CellStyle()["align"]?.GetValue<string>(); await FormatSelectionAsync("align", JsonValue.Create(now == value ? "general" : value)!); };
        Align("CellAlignLeftButton", "left"); Align("CellAlignCenterButton", "center"); Align("CellAlignRightButton", "right");
        void Colors(string name, string key, (string Label, string Value)[] list)
        {
            var box = this.FindControl<ComboBox>(name)!;
            foreach (var (label, value) in list) box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
            box.SelectedIndex = 0;
            box.SelectionChanged += async (_, _) => { if (rendering || box.SelectedItem is not ComboBoxItem item) return; await FlushAsync(); await FormatSelectionAsync(key, JsonValue.Create((string)item.Tag!)!); };
        }
        Colors("CellColorBox", "color", TextColors); Colors("CellFillBox", "fill", FillColors);
    }

    private void SetupFunctionCatalog()
    {
        var box = this.FindControl<ComboBox>("FunctionCatalogBox")!;
        foreach (var (label, formula) in FunctionCatalog) box.Items.Add(new ComboBoxItem { Content = label, Tag = formula });
        box.SelectedIndex = 0;
        this.FindControl<TextBlock>("FunctionHint")!.Text = $"{FunctionCatalog.Length} fonctions au catalogue, plus les alias anglais et français à saisir à la main.";
        this.FindControl<Button>("InsertFunctionButton")!.Click += async (_, _) => { if (box.SelectedItem is ComboBoxItem item) await InsertFunctionAsync((string)item.Tag!); };
    }

    // Writes the formula under a multi-cell selection, or in the active cell using the cells above it.
    public async Task InsertFunctionAsync(string template)
    {
        await FlushAsync();
        var range = Surface.Selection; string target, rangeText, cellText;
        if (range.Count > 1)
        {
            target = Controls.SheetGrid.CellName(range.Right, range.Bottom + 1); rangeText = Surface.SelectionAddress; cellText = Controls.SheetGrid.CellName(range.Left, range.Top);
        }
        else
        {
            target = active; rangeText = Surface.Row > 0 ? $"{Controls.SheetGrid.CellName(Surface.Column, 0)[..^1]}1:{Controls.SheetGrid.CellName(Surface.Column, Surface.Row - 1)}" : "A1:A10";
            cellText = Surface.Row > 0 ? Controls.SheetGrid.CellName(Surface.Column, Surface.Row - 1) : "A1";
        }
        var input = template.Replace("{r}", rangeText).Replace("{c}", cellText);
        await Enqueue(() => Document.ExecuteAsync("SetCell", new { address = target, input }));
        if (target != active) Surface.Select(range.Right, Math.Min(range.Bottom + 1, 1048575));
        this.FindControl<TextBlock>("SelectionHint")!.Text = $"Fonction insérée en {target}.";
    }

    private void UpdateRichStyles()
    {
        var style = CellStyle(); var on = Brush.Parse(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light ? "#DCE8FC" : "#426497");
        void Mark(string name, bool value) => this.FindControl<Button>(name)!.Background = value ? on : Brushes.Transparent;
        Mark("CellItalicButton", StyleFlag(style, "italic")); Mark("CellUnderlineButton", StyleFlag(style, "underline"));
        var align = style["align"]?.GetValue<string>() ?? "general";
        Mark("CellAlignLeftButton", align == "left"); Mark("CellAlignCenterButton", align == "center"); Mark("CellAlignRightButton", align == "right");
        void Select(string name, string key) { var box = this.FindControl<ComboBox>(name)!; var value = style[key]?.GetValue<string>() ?? "none"; box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == value) ?? box.Items[0]; }
        Select("CellColorBox", "color"); Select("CellFillBox", "fill");
    }
}
