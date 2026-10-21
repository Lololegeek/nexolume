using System.Text.Json.Nodes;
namespace OfficeBridge.Deltas;

public static class DocumentProjection
{
    public static void Apply(JsonObject snapshot, JsonObject reply)
    {
        var revision = reply["revision"]!.GetValue<long>();
        if (revision != snapshot["revision"]!.GetValue<long>() + 1) throw new InvalidOperationException("Delta revision mismatch");
        var content = snapshot["content"]!.AsObject();
        foreach (var change in reply["changes"]!.AsArray())
        {
            var data = change!["data"]!; var kind = change["kind"]!.GetValue<string>();
            switch (kind)
            {
                case "DocumentRenamed": snapshot["title"] = data["title"]!.DeepClone(); break;
                case "PageLayoutChanged": snapshot["page_layout"] = data["layout"]?.DeepClone(); break;
                case "FootnotesChanged": snapshot["footnotes"] = data["footnotes"]?.DeepClone(); break;
                case "CommentsChanged": snapshot["comments"] = data["comments"]?.DeepClone(); break;
                case "RevisionsChanged": snapshot["revisions"] = data["revisions"]?.DeepClone(); break;
                case "SheetMetaChanged": snapshot["sheet_meta"] = data["meta"]?.DeepClone(); break;
                case "ParagraphChanged": Replace(content["paragraphs"]!.AsArray(), data["paragraph"]!); break;
                case "ParagraphInserted": content["paragraphs"]!.AsArray().Insert(data["index"]!.GetValue<int>(), data["paragraph"]!.DeepClone()); break;
                case "ParagraphRemoved": Remove(content["paragraphs"]!.AsArray(), data["id"]!.GetValue<string>()); break;
                case "CellChanged":
                case "CellRecalculated":
                    var cells = content["cells"]!.AsObject(); var address = data["address"]!.GetValue<string>();
                    if (data["cell"] is null) cells.Remove(address); else cells[address] = data["cell"]!.DeepClone(); break;
                case "SlideAdded": content["slides"]!.AsArray().Insert(data["index"]!.GetValue<int>(), data["slide"]!.DeepClone()); break;
                case "SlideReordered": Move(content["slides"]!.AsArray(), data["id"]!.GetValue<string>(), data["index"]!.GetValue<int>()); break;
                case "SlideRemoved": Remove(content["slides"]!.AsArray(), data["id"]!.GetValue<string>()); break;
                case "SlideUpdated":
                    var changed = content["slides"]!.AsArray().First(s => s!["id"]!.GetValue<string>() == data["id"]!.GetValue<string>())!;
                    foreach (var key in new[] { "background", "notes", "transition" }) changed[key] = data[key]!.DeepClone();
                    break;
                case "ObjectReordered":
                case "ObjectUpdated":
                case "ObjectMoved":
                case "ObjectAdded":
                case "ObjectRemoved":
                    var slide = content["slides"]!.AsArray().First(s => s!["id"]!.GetValue<string>() == data["slide"]!.GetValue<string>())!;
                    var objects = slide["objects"]!.AsArray();
                    if (kind == "ObjectReordered") Move(objects, data["id"]!.GetValue<string>(), data["index"]!.GetValue<int>());
                    else if (kind == "ObjectAdded") objects.Insert(data["index"]!.GetValue<int>(), data["object"]!.DeepClone());
                    else if (kind == "ObjectRemoved") Remove(objects, data["id"]!.GetValue<string>());
                    else Replace(objects, data["object"]!); break;
                default: throw new InvalidOperationException($"Unsupported delta: {kind}");
            }
        }
        snapshot["revision"] = revision;
    }
    private static void Move(JsonArray items, string id, int index) { var old = Find(items, id); var item = items[old]!.DeepClone(); items.RemoveAt(old); items.Insert(index, item); }
    private static void Replace(JsonArray items, JsonNode item) { var index = Find(items, item["id"]!.GetValue<string>()); items[index] = item.DeepClone(); }
    private static void Remove(JsonArray items, string id) => items.RemoveAt(Find(items, id));
    private static int Find(JsonArray items, string id) { for (int i = 0; i < items.Count; i++) if (items[i]!["id"]!.GetValue<string>() == id) return i; throw new InvalidOperationException("Delta element missing"); }
}
