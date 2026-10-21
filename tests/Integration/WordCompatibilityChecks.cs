using OfficeBridge.Services;
using System.IO.Compression;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

internal static class WordCompatibilityChecks
{
    public static void ValidatePackage(string path)
    {
        using var package = WordprocessingDocument.Open(path, false);
        var errors = new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(package).Take(12).ToArray();
        if (errors.Length != 0) throw new Exception("Open XML validation failed for " + path + ":\n" + string.Join("\n", errors.Select(e => e.Description + " " + e.Path?.XPath)));
    }
    public static async Task Run(string input, string output)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        using var engine = new OfficeEngine(); using var doc = await DocumentSession.OpenAsync(engine, input);
        var paragraphs = doc.Snapshot["content"]!["paragraphs"]!.AsArray();
        Check(paragraphs[0]!["style"]!["font_size"]!.GetValue<double>() == 32, "Word 24 pt heading must be 32 logical pixels");
        Check(paragraphs[0]!["style"]!["bold"]!.GetValue<bool>(), "Inherited heading weight");
        var unchanged = engine.ExportOffice(doc.Id, doc.Revision, "docx"); var originalBytes = await File.ReadAllBytesAsync(input); Check(unchanged.SequenceEqual(originalBytes), "Unchanged DOCX must be byte identical");
        var paragraph = paragraphs.First(p => p!["text"]!.GetValue<string>().StartsWith("Une ligne à modifier"))!;
        await doc.ExecuteAsync("SetParagraph", new { id = paragraph["id"]!.GetValue<string>(), text = "Une ligne modifiée dans Nexolume sans perdre la mise en forme." });
        var editedStyle = paragraph["style"]!.DeepClone(); editedStyle["font_size"] = 20; editedStyle["line_spacing"] = 1.5; editedStyle["color"] = "#285CB3";
        await doc.ExecuteAsync("SetParagraph", new { id = paragraph["id"]!.GetValue<string>(), style = editedStyle });
        var selectedStart="Une ligne modifiée dans Nexolume sans perdre la mise en forme.".IndexOf("Nexolume",StringComparison.Ordinal);
        await doc.ExecuteAsync("FormatText",new {id=paragraph["id"]!.GetValue<string>(),start=selectedStart,end=selectedStart+8,style=new {bold=true,font_family="Georgia",font_size=32,color="#B3263D",highlight="#FFF1A8"}});
        await doc.ExecuteAsync("InsertParagraph", new { id = "nexolume-extra", index = 3, text = "Paragraphe ajouté depuis Nexolume" });
        var native = System.IO.Path.ChangeExtension(output, ".oswriter"); await doc.SaveAsync(native);
        using var loaded = await DocumentSession.OpenAsync(engine, native); await loaded.SaveAsync(output);
        using var source = ZipFile.OpenRead(input); using var result = ZipFile.OpenRead(output);
        foreach (var entry in source.Entries.Where(e => e.FullName != "word/document.xml"))
        {
            var retained = result.GetEntry(entry.FullName); Check(retained is not null, "Lost DOCX part " + entry.FullName);
            using var a = entry.Open(); using var b = retained!.Open(); using var left = new MemoryStream(); using var right = new MemoryStream(); a.CopyTo(left); b.CopyTo(right);
            Check(left.ToArray().SequenceEqual(right.ToArray()), "Modified unedited DOCX part " + entry.FullName);
        }
        Console.WriteLine("PASS real Word fixture: inherited styles/point units, byte-identical no-op, edited text and inserted paragraph, native intermediary, every other ZIP part unchanged");
        using var created = await DocumentSession.CreateAsync(engine, "writer", "Créé dans Nexolume");
        var titleId = created.Snapshot["content"]!["paragraphs"]![0]!["id"]!.GetValue<string>();
        await created.ExecuteAsync("SetParagraph", new { id = titleId, text = "Un document créé dans Nexolume", style = new { font_size = 32, bold = true } });
        await created.ExecuteAsync("InsertParagraph", new { id = "native-bullet", index = 1, text = "Une puce native", style = new { font_size = 16, font_family = "Calibri", list = "bullet", line_spacing = 1.5, highlight = "#FFF1A8" } });
        const string richText="Texte normal, passage enrichi, retour normal.";
        await created.ExecuteAsync("InsertParagraph",new {id="rich-native",index=2,text=richText});
        var richStart=richText.IndexOf("passage enrichi",StringComparison.Ordinal);
        await created.ExecuteAsync("FormatText",new {id="rich-native",start=richStart,end=richStart+15,style=new {bold=true,italic=true,underline=true,font_family="Georgia",font_size=24,color="#285CB3",highlight="#FFF1A8"}});
        var freshPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(output)!, "word-reference-new.docx"); await created.SaveAsync(freshPath);
        await created.SaveAsync(System.IO.Path.ChangeExtension(freshPath,".odt"));
        ValidatePackage(output); ValidatePackage(freshPath); Console.WriteLine("PASS Microsoft Open XML SDK schema validation: preserved DOCX and newly generated DOCX");
        var added = loaded.Snapshot["content"]!["paragraphs"]!.AsArray().First(p => p!["id"]!.GetValue<string>() == "nexolume-extra")!;
        var numberedStyle = added["style"]!.DeepClone(); numberedStyle["list"] = "number";
        await loaded.ExecuteAsync("SetParagraph", new { id = "nexolume-extra", style = numberedStyle });
        var numberedPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(output)!, "word-reference-numbered.docx"); await loaded.SaveAsync(numberedPath); ValidatePackage(numberedPath);
        Console.WriteLine("PASS Microsoft Open XML SDK schema validation: new list added to Word package with existing numbering");
        await created.ExecuteAsync("SetPageLayout", new { layout = new { width = 559.37, height = 793.7, top = 37.795, right = 37.795, bottom = 37.795, left = 37.795 } });
        var richStyle = created.Snapshot["content"]!["paragraphs"]![2]!["style"]!.DeepClone(); richStyle["page_break_before"] = true;
        await created.ExecuteAsync("SetParagraph", new { id = "rich-native", style = richStyle });
        var pagePath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(output)!, "word-reference-page.docx"); await created.SaveAsync(pagePath); ValidatePackage(pagePath);
        await created.SaveAsync(System.IO.Path.ChangeExtension(pagePath,".odt"));
        await loaded.ExecuteAsync("SetPageLayout", new { layout = new { width = 816, height = 1056, top = 48, right = 72, bottom = 60, left = 84 } });
        var editedPagePath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(output)!,"word-reference-page-edited.docx"); await loaded.SaveAsync(editedPagePath); ValidatePackage(editedPagePath);
    }
}
