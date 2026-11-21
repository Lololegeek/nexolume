using System.Text.Json;
namespace Nexolume.Desktop.Services;

public sealed class Preferences
{
    public List<string> Recent { get; set; } = [];
    public bool Light { get; set; }
    public string Language { get; set; } = "fr";
    // %LOCALAPPDATA%\Nexolume\preferences.json on Windows, localStorage in the browser.
    public static Preferences Load() { try { return Platform.AppPlatform.Current.ReadSetting("preferences") is { } json ? JsonSerializer.Deserialize<Preferences>(json) ?? new() : new(); } catch { return new(); } }
    public void Save() { try { Platform.AppPlatform.Current.WriteSetting("preferences", JsonSerializer.Serialize(this)); } catch (Exception ex) { AppLog.Write(ex); } }
    public void Remember(string path) { Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)); Recent.Insert(0, path); Recent = Recent.Take(12).ToList(); Save(); }
}
