using System.Text.Json;
using Nexolume.Desktop.Services;

namespace Nexolume.Desktop.Editors.Writer;

public sealed class WritingOptions
{
    public bool SentenceCapitals { get; set; } = true;
    public bool DoubleCapitals { get; set; } = true;
    public bool CommonTypos { get; set; } = true;
    public bool Typography { get; set; } = true;
    public bool AutomaticLists { get; set; } = true;
    public bool UnderlineSpelling { get; set; } = true;
    public string SpellingLanguage { get; set; } = "fr-FR";
    public Dictionary<string, List<string>> Dictionary { get; set; } = [];
    public Dictionary<string, string> Replacements { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public static WritingOptions Current { get; } = Load();
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexolume", "writing.json");
    private static WritingOptions Load()
    {
        try
        {
            var options = JsonSerializer.Deserialize<WritingOptions>(File.ReadAllText(FilePath)) ?? new();
            options.Dictionary ??= [];
            if (options.SpellingLanguage is not ("fr-FR" or "en-US")) options.SpellingLanguage = "fr-FR";
            options.Replacements = (options.Replacements ?? []).Where(p => p.Key.Length <= 40 && !string.IsNullOrWhiteSpace(p.Value) && p.Value.Length <= 200).Take(100).ToDictionary(p => p.Key, p => p.Value);
            return options;
        }
        catch { return new(); }
    }
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this));
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        catch (Exception ex) { AppLog.Write(ex); }
    }
}
