using System.Text.Json;
namespace Nexolume.Desktop.Services;

/// <summary>Recent and favourite fonts of the font picker, kept apart from <see cref="Preferences"/> so each is saved on its own.</summary>
public sealed class FontPreferences
{
    public List<string> Recent { get; set; } = [];
    public List<string> Favorites { get; set; } = [];

    private static FontPreferences? current;
    public static FontPreferences Current => current ??= Load();

    private static FontPreferences Load()
    {
        try
        {
            if (Platform.AppPlatform.Current.ReadSetting("fonts") is not { } json) return new();
            using var document = JsonDocument.Parse(json);
            static List<string> Read(JsonElement root, string name) => root.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToList() : [];
            return new() { Recent = Read(document.RootElement, "recent"), Favorites = Read(document.RootElement, "favorites") };
        }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var (name, list) in new[] { ("recent", Recent), ("favorites", Favorites) }) { writer.WriteStartArray(name); foreach (var font in list) writer.WriteStringValue(font); writer.WriteEndArray(); }
                writer.WriteEndObject();
            }
            Platform.AppPlatform.Current.WriteSetting("fonts", System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
        }
        catch (Exception ex) { AppLog.Write(ex); }
    }

    public void Remember(string font) { Recent.RemoveAll(f => string.Equals(f, font, StringComparison.OrdinalIgnoreCase)); Recent.Insert(0, font); if (Recent.Count > 6) Recent.RemoveRange(6, Recent.Count - 6); Save(); }

    public void SetFavorite(string font, bool on)
    {
        Favorites.RemoveAll(f => string.Equals(f, font, StringComparison.OrdinalIgnoreCase));
        if (on) Favorites.Add(font);
        Save();
    }
}
