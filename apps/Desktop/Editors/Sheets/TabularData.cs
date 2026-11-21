using System.Text;

namespace Nexolume.Desktop.Editors.Sheets;

public static class TabularData
{
    public static char DetectSeparator(string text)
    {
        var counts = new Dictionary<char, int> { [';'] = 0, [','] = 0, ['\t'] = 0 };
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '"') { if (quoted && i + 1 < text.Length && text[i + 1] == '"') i++; else quoted = !quoted; }
            else if (!quoted && text[i] is '\r' or '\n') break;
            else if (!quoted && counts.ContainsKey(text[i])) counts[text[i]]++;
        }
        return counts.MaxBy(c => c.Value).Key;
    }

    public static List<List<string>> Parse(string text, char separator)
    {
        if (text.Length > 1_000_000) throw new InvalidOperationException("Le tableau dépasse la limite de 1 Mo de texte.");
        if (separator is not (';' or ',' or '\t')) throw new ArgumentException("Séparateur invalide.");
        var rows = new List<List<string>>(); var row = new List<string>(); var field = new StringBuilder();
        bool quoted = false, closed = false; int cells = 0;
        void Field() { row.Add(field.ToString()); field.Clear(); closed = false; if (++cells > 1000) throw new InvalidOperationException("L’import et le collage sont limités à 1 000 cellules."); }
        for (int i = text.StartsWith('\uFEFF') ? 1 : 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; } else { quoted = false; closed = true; } }
                else field.Append(ch);
            }
            else if (ch == separator) Field();
            else if (ch is '\r' or '\n')
            {
                Field(); rows.Add(row); row = [];
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else if (closed) throw new InvalidOperationException("Caractère inattendu après les guillemets d’une cellule.");
            else if (ch == '"' && field.Length == 0) quoted = true;
            else field.Append(ch);
        }
        if (quoted) throw new InvalidOperationException("Le tableau contient des guillemets non fermés.");
        if (field.Length > 0 || row.Count > 0 || closed || rows.Count == 0) { Field(); rows.Add(row); }
        return rows;
    }

    public static string Write(IEnumerable<IEnumerable<string>> rows, char separator)
    {
        string Escape(string s) => s.IndexOfAny([separator, '\r', '\n', '"']) >= 0 ? '"' + s.Replace("\"", "\"\"") + '"' : s;
        return string.Join("\r\n", rows.Select(row => string.Join(separator, row.Select(Escape))));
    }
}
