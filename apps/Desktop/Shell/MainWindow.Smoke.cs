using Avalonia.Controls;
namespace Nexolume.Desktop.Shell;

public partial class MainWindow
{
    internal async Task SmokeAsync(string output)
    {
        var path = Path.Combine(Path.GetTempPath(), "Nexolume-native-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        try
        {
            foreach (var kind in new[] { "writer", "sheets", "slides" })
            {
                await CreateAsync(kind); var tab = (TabItem)tabs.SelectedItem!; var view = editors[tab]; var doc = view.Document;
                switch (kind)
                {
                    case "writer":
                        await doc.ExecuteAsync("SetParagraph", new { id = doc.Snapshot["content"]!["paragraphs"]![0]!["id"]!.GetValue<string>(), text = "Windows natif — été 🦀" });
                        using (var layout = new Editors.Writer.Printing.WriterPrintLayout(doc.Snapshot)) await layout.ExportPdfAsync(Path.ChangeExtension(output,".pdf"));
                        break;
                    case "sheets": await doc.ExecuteAsync("SetCell", new { address = "A1", input = "=10+20" }); break;
                    case "slides": await doc.ExecuteAsync("AddSlide", new { id = Guid.NewGuid().ToString() }); break;
                }
                var extension = kind switch { "writer" => "oswriter", "sheets" => "ossheet", _ => "osslides" };
                await doc.SaveAsync(Path.Combine(path, "native." + extension)); await CloseTabAsync(tab);
            }
            await File.WriteAllTextAsync(output, "PASS: Windows native Avalonia startup; Writer, Sheets, Slides; Rust commands; PDF export; save and clean close.");
        }
        finally { Directory.Delete(path, true); }
    }
}
