using OfficeBridge.Services;
using System.Text.Json.Nodes;
if (args.Length == 3 && args[0] == "--word-fixture") { await WordCompatibilityChecks.Run(args[1], args[2]); return; }
var temp = Path.Combine(Path.GetTempPath(), "Nexolume-" + Guid.NewGuid()); Directory.CreateDirectory(temp);
void Check(bool v, string m) { if (!v) throw new Exception(m); }
try
{
    using var e = new OfficeEngine(); using var d = await DocumentSession.CreateAsync(e, "writer", "Été 🦀");
    var id = d.Snapshot["content"]!["paragraphs"]![0]!["id"]!.GetValue<string>();
    await d.ExecuteAsync("SetParagraph", new { id, text = "Bonjour été 🦀", group = "typing" });
    await d.ExecuteAsync("SetParagraph", new { id, text = "Bonjour été 🦀 !", group = "typing" });
    await d.ExecuteAsync("Undo"); Check(d.Snapshot["content"]!["paragraphs"]![0]!["text"]!.GetValue<string>() == "", "grouped undo");
    await d.ExecuteAsync("Redo"); await d.ExecuteAsync("SetParagraph", new { id, style = new { bold = true, italic = true, underline = true } });
    var path = Path.Combine(temp, "writer.oswriter"); await d.SaveAsync(path); Check(!d.Dirty, "saved indicator"); await d.SaveAsync(); Check(File.Exists(path + ".bak"), "overwrite backup");
    using var reopened = await DocumentSession.OpenAsync(e, path); Check(JsonNode.DeepEquals(d.Snapshot["content"], reopened.Snapshot["content"]), "Writer roundtrip"); Check(d.Id != reopened.Id, "independent handles");
    foreach (var extension in new[] { ".docx", ".odt" })
    {
        var officePath = Path.Combine(temp, "writer" + extension);
        await d.SaveAsync(officePath);
        if (extension == ".docx") WordCompatibilityChecks.ValidatePackage(officePath);
        var exportDirectory = Environment.GetEnvironmentVariable("NEXOLUME_OFFICE_FORMATS_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(exportDirectory)) { Directory.CreateDirectory(exportDirectory); File.Copy(officePath, Path.Combine(exportDirectory, "writer" + extension), true); }
        Check(File.ReadAllBytes(officePath).Take(2).SequenceEqual(new byte[] { 0x50, 0x4b }), extension + " is a ZIP package");
        using var officeCopy = await DocumentSession.OpenAsync(e, officePath);
        Check(officeCopy.Kind == "writer" && officeCopy.Snapshot["content"]!["paragraphs"]![0]!["text"]!.GetValue<string>() == "Bonjour été 🦀 !", extension + " import");
        Check(officeCopy.Snapshot["content"]!["paragraphs"]![0]!["style"]!["bold"]!.GetValue<bool>() && officeCopy.Snapshot["content"]!["paragraphs"]![0]!["style"]!["italic"]!.GetValue<bool>() && officeCopy.Snapshot["content"]!["paragraphs"]![0]!["style"]!["underline"]!.GetValue<bool>(), extension + " text formatting");
        await officeCopy.ExecuteAsync("SetParagraph", new { id = officeCopy.Snapshot["content"]!["paragraphs"]![0]!["id"]!.GetValue<string>(), text = "Modifié puis réenregistré" });
        await officeCopy.SaveAsync();
        using var verified = await DocumentSession.OpenAsync(e, officePath);
        Check(verified.Snapshot["content"]!["paragraphs"]![0]!["text"]!.GetValue<string>() == "Modifié puis réenregistré", extension + " edit/export/reopen");
    }
    await d.ExecuteAsync("InsertParagraph", new { id = "second", index = 1, text = "Deuxième paragraphe" }); await d.ExecuteAsync("DeleteParagraph", new { id = "second" }); await d.ExecuteAsync("Undo"); Check(d.Snapshot["content"]!["paragraphs"]!.AsArray().Count == 2, "delete undo");
    for (int i = 0; i < 100; i++) { using var cycle = await DocumentSession.CreateAsync(e, "writer", "cycle"); await cycle.ExecuteAsync("RenameDocument", new { title = "Été 🦀" }); }
    Console.WriteLine("PASS Writer: UTF-8, grouped undo, redo, styles, deltas, native + DOCX/ODT edit/export/reopen, 100 lifecycles");
    await BoundaryChecks.Run(temp);
    LegacyCompatibility.Run();
}
finally { Directory.Delete(temp, true); }
