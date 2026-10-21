using OfficeBridge.Services;
using OfficeBridge.Models;
using OfficeBridge.Deltas;
using System.Text.Json.Nodes;
internal static class BoundaryChecks
{
    public static async Task Run(string temp)
    {
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        using var engine = new OfficeEngine(); using var doc = await DocumentSession.CreateAsync(engine, "writer", "Robustesse");
        var id = doc.Snapshot["content"]!["paragraphs"]![0]!["id"]!.GetValue<string>();
        await doc.ExecuteAsync("SetParagraph", new { id, text = "Texte à conserver" }); var path = Path.Combine(temp, "preserved.oswriter"); await doc.SaveAsync(path); var original = await File.ReadAllTextAsync(path);
        await doc.ExecuteAsync("SetParagraph", new { id, text = "Nouvelle version" });
        try { await doc.SaveAsync(Path.Combine(temp, "missing", "fail.oswriter")); throw new Exception("Save falsely succeeded"); } catch (DirectoryNotFoundException) { }
        Check(doc.Dirty && await File.ReadAllTextAsync(path) == original, "Save failure must preserve old file and dirty state");
        var revision = doc.Revision;
        try { await doc.ExecuteAsync("Batch", new { commands = new object[] { new { command = "SetParagraph", @params = new { id, text = "Rollback" } }, new { command = "DeleteParagraph", @params = new { id = "unknown" } } } }); throw new Exception("Transaction accepted"); } catch (OfficeException) { }
        Check(doc.Revision == revision && doc.Snapshot["content"]!["paragraphs"]![0]!["text"]!.GetValue<string>() == "Nouvelle version", "Transaction atomicity");
        try { DocumentProjection.Apply(doc.Snapshot, new JsonObject { ["revision"] = 999, ["changes"] = new JsonArray() }); throw new Exception("Delta accepted"); } catch (InvalidOperationException) { }
        engine.Send(new { protocol = 1, command = "RenameDocument", document = doc.Id, expected_revision = doc.Revision, @params = new { title = "External revision" } });
        try { await doc.ExecuteAsync("RenameDocument", new { title = "Stale" }); throw new Exception("Stale edit accepted"); } catch (OfficeException ex) when (ex.Code == "RevisionMismatch") { }
        Check(doc.Title == "External revision" && doc.CanUndo && doc.Dirty, "Snapshot resynchronization");
        var malformed = Path.Combine(temp, "invalid.oswriter"); await File.WriteAllTextAsync(malformed, "{\"schema\":99}");
        try { using var invalid = await DocumentSession.OpenAsync(engine, malformed); throw new Exception("Invalid file accepted"); } catch (OfficeException) { }
        using var sheet = await DocumentSession.CreateAsync(engine, "sheets", "Recalcul"); await sheet.ExecuteAsync("SetCell", new { address = "A1", input = "=10+20" });
        var sheetPath = Path.Combine(temp, "recalc.ossheet"); await sheet.SaveAsync(sheetPath); var json = JsonNode.Parse(await File.ReadAllTextAsync(sheetPath))!; json["content"]!["cells"]!["A1"]!["display"] = "WRONG"; await File.WriteAllTextAsync(sheetPath, json.ToJsonString()); using var loaded = await DocumentSession.OpenAsync(engine, sheetPath); Check(loaded.Snapshot["content"]!["cells"]!["A1"]!["display"]!.GetValue<string>() == "30", "Recalculate untrusted stored display");
        await Task.WhenAll(Enumerable.Range(0, 24).Select(async i => { using var independent = await DocumentSession.CreateAsync(engine, "writer", "Parallel " + i); await independent.ExecuteAsync("RenameDocument", new { title = "Unicode été 🦀 " + i }); }));
        Console.WriteLine("PASS boundaries: atomic rollback, save failure, invalid files, revisions/resync, recomputation on open, 24 concurrent sessions");
    }
}
