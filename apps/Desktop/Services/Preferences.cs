using System.Text.Json;
namespace Nexolume.Desktop.Services;

public sealed class Preferences
{
    public List<string> Recent { get; set; } = [];
    public bool Light { get; set; }
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexolume", "preferences.json");
    public static Preferences Load() { try { return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath)) ?? new(); } catch { return new(); } }
    public void Save() { try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); var temp = FilePath + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(this)); File.Move(temp, FilePath, true); } catch (Exception ex) { AppLog.Write(ex); } }
    public void Remember(string path) { Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)); Recent.Insert(0, path); Recent = Recent.Take(12).ToList(); Save(); }
}
