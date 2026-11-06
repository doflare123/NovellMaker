using System.Text;

namespace NovellMaker.Editor.Services;

internal static class DelimitedText
{
    internal static List<string[]> Read(string text, char separator)
    {
        var rows = new List<string[]>(); var row = new List<string>(); var field = new StringBuilder();
        var quoted = false; var closed = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else { quoted = false; closed = true; }
                }
                else field.Append(c);
                continue;
            }
            if (c == '"' && field.Length == 0 && !closed) { quoted = true; continue; }
            if (c == separator || c is '\r' or '\n')
            {
                row.Add(field.ToString()); field.Clear(); closed = false;
                if (c != separator)
                {
                    if (row.Count != 1 || row[0].Length > 0) rows.Add(row.ToArray());
                    row.Clear();
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                }
            }
            else
            {
                if (closed || c == '"') throw new InvalidDataException("Некорректные кавычки CSV/TSV.");
                field.Append(c);
            }
        }
        if (quoted) throw new InvalidDataException("Незакрытые кавычки CSV/TSV.");
        if (field.Length > 0 || row.Count > 0 || closed) { row.Add(field.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }
}
