using OfficeBridge.Services;
static class LegacyCompatibility
{
    public static void Run()
    {
        using var engine = new OfficeEngine(); var writer = """{"schema":1,"id":"legacy-writer","title":"Ancien document","revision":0,"content":{"kind":"writer","paragraphs":[{"id":"p","text":"Été 🦀","style":{"bold":true,"italic":false,"underline":false}}]}}""";
        var old = engine.Send(new { protocol = 1, command = "OpenDocument", @params = new { json = writer } }); var doc = old["snapshot"]!; if (doc["schema"]!.GetValue<int>() != 2 || !doc["content"]!["paragraphs"]![0]!["style"]!["bold"]!.GetValue<bool>() || doc["content"]!["paragraphs"]![0]!["style"]!["color"]!.GetValue<string>() != "#111111") throw new Exception("Legacy Writer migration failed");
        var sheet = """{"schema":1,"id":"legacy-sheet","title":"Ancien tableur","revision":0,"content":{"kind":"sheets","cells":{"A1":{"input":"=10+20","display":"obsolete"}}}}""";
        var result = engine.Send(new { protocol = 1, command = "OpenDocument", @params = new { json = sheet } }); if (result["snapshot"]!["content"]!["cells"]!["A1"]!["display"]!.GetValue<string>() != "30" || result["snapshot"]!["content"]!["cells"]!["A1"]!["style"]!["format"]!.GetValue<string>() != "general") throw new Exception("Legacy Sheets migration failed"); Console.WriteLine("PASS schema 1 -> 2: old Writer styles and Sheets formulas preserved");
    }
}
