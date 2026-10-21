using OfficeBridge.Services;
using Nexolume.Desktop.Editors.Writer.Printing;

namespace Nexolume.Desktop.Services;

internal static class BatchOutput
{
    // --export-pdf input output; --print input printer [output-file]
    public static bool Handles(string[] args) => args.Length > 0 && args[0] is "--export-pdf" or "--print";
    public static async Task RunAsync(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("Utilisation : --export-pdf document.oswriter sortie.pdf ; --print document imprimante [fichier-de-sortie]");
        using var engine = new OfficeEngine(); using var document = await DocumentSession.OpenAsync(engine, Path.GetFullPath(args[1]));
        if (document.Kind != "writer") throw new NotSupportedException("La sortie paginée est réservée à Writer.");
        using var layout = new WriterPrintLayout(document.Snapshot);
        if (args[0] == "--export-pdf") await layout.ExportPdfAsync(args[2]);
        else await WindowsPrinter.PrintAsync(layout, printer: args[2], output: args.Length > 3 ? Path.GetFullPath(args[3]) : null);
    }
}
