using System.Globalization;
using System.Text.RegularExpressions;

namespace Nexolume.Desktop.Editors.Writer;

public static class TypingAssistance
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly Dictionary<string, string> Replacements = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bonjoru"] = "bonjour", ["bonjout"] = "bonjour", ["mercu"] = "merci",
        ["beaucoups"] = "beaucoup", ["bocoup"] = "beaucoup", ["toujorus"] = "toujours",
        ["jammais"] = "jamais", ["aujoud'hui"] = "aujourd’hui", ["aujourdhui"] = "aujourd’hui",
        ["parmis"] = "parmi", ["malgrès"] = "malgré", ["apparament"] = "apparemment",
        ["vraimment"] = "vraiment", ["seulementt"] = "seulement", ["dévellopement"] = "développement",
        ["developpement"] = "développement", ["adressse"] = "adresse", ["addresse"] = "adresse",
        ["acceuil"] = "accueil", ["language"] = "langage", ["connection"] = "connexion",
        ["cest"] = "c’est", ["jai"] = "j’ai", ["jétais"] = "j’étais", ["ca"] = "ça",
        ["etre"] = "être", ["tres"] = "très", ["deja"] = "déjà", ["aout"] = "août",
        ["coeur"] = "cœur", ["oeuvre"] = "œuvre"
    };
    // Ambiguous words (a/à, ou/où, du/dû...) deliberately require the author's choice.
    private static bool ProtectedToken(string prefix)
    {
        var token = prefix.TrimEnd(); var index = token.LastIndexOfAny([' ', '\t', '\r', '\n']);
        token = token[(index + 1)..];
        return token.Contains('@') || token.Contains('/') || token.Contains('\\') || token.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
    }

    public static string Correct(string text, int caret, string input, WritingOptions options)
    {
        if (input.Length != 1 || caret < 1 || caret > text.Length) return text;
        var prefix = text[..caret]; var suffix = text[caret..]; var ch = input[0];
        if (ProtectedToken(prefix.TrimEnd())) return text;
        if (options.SentenceCapitals && char.IsLower(ch))
        {
            var before = prefix[..^1].TrimEnd(' ', '\t', '“', '«', '"', '(', '[', '‘');
            var start = before.Length == 0 || before.EndsWith('\n');
            if (!start && char.IsWhiteSpace(prefix.Length > 1 ? prefix[^2] : '\0'))
            {
                var sentence = before.TrimEnd('”', '»', '"', ')', ']');
                if (sentence.EndsWith('!') || sentence.EndsWith('?') || sentence.EndsWith('…')) start = true;
                else if (sentence.EndsWith('.'))
                {
                    var word = Regex.Match(sentence, @"([\p{L}]+)\.$").Groups[1].Value;
                    start = word.Length > 1 && !new[] { "M", "Mme", "Mlle", "Dr", "Pr", "etc", "ex", "env", "cf", "p", "n" }.Contains(word, StringComparer.OrdinalIgnoreCase);
                }
            }
            if (start) prefix = prefix[..^1] + char.ToUpper(ch, French);
        }
        if (char.IsWhiteSpace(ch) || ".,;:!?)]}»\"".Contains(ch))
        {
            var match = Regex.Match(prefix[..^1], @"[\p{L}][\p{L}’']*$");
            if (match.Success)
            {
                var token = match.Value; var replacement = token;
                var custom = options.Replacements.FirstOrDefault(pair => string.Equals(pair.Key, token, StringComparison.OrdinalIgnoreCase));
                if (options.CommonTypos && !(token.Length > 1 && token.All(char.IsUpper)) && (custom.Value is { Length: > 0 } || Replacements.ContainsKey(token)))
                {
                    var corrected = custom.Value is { Length: > 0 } ? custom.Value : Replacements[token];
                    replacement = token.All(char.IsUpper) ? corrected.ToUpper(French) : char.IsUpper(token[0]) ? char.ToUpper(corrected[0], French) + corrected[1..] : corrected;
                }
                if (options.DoubleCapitals && replacement.Length >= 3 && char.IsUpper(replacement[0]) && char.IsUpper(replacement[1]) && char.IsLower(replacement[2]) && replacement[2..].All(char.IsLower))
                    replacement = replacement[..1] + replacement[1..].ToLower(French);
                prefix = prefix[..match.Index] + replacement + ch;
            }
        }
        if (options.Typography)
        {
            if (ch == '\'' && prefix.Length >= 2 && char.IsLetter(prefix[^2])) prefix = prefix[..^1] + "’";
            foreach (var (from, to) in new[] { ("...", "…"), ("(c)", "©"), ("(r)", "®"), ("(tm)", "™"), ("-> ", "→ "), ("<- ", "← ") })
                if (prefix.EndsWith(from, StringComparison.OrdinalIgnoreCase)) { prefix = prefix[..^from.Length] + to; break; }
        }
        return prefix + suffix;
    }

    public static string ChangeCase(string text, string mode) => mode switch
    {
        "upper" => text.ToUpper(French), "lower" => text.ToLower(French),
        "title" => French.TextInfo.ToTitleCase(text.ToLower(French)),
        _ => Regex.Replace(text.ToLower(French), @"(^[\s«“""(]*|[.!?…]\s+)(\p{L})", m => m.Groups[1].Value + m.Groups[2].Value.ToUpper(French))
    };
}
