using System.Net;
using System.Text.RegularExpressions;

namespace Cdiary;

public sealed record NoteHeading(string Id, string Text, int Level, int Line = 0, int Offset = 0)
{
    public System.Windows.Thickness Indent => new(Math.Clamp(Level - 1, 0, 5) * 8, 0, 0, 0);
}

public static class NoteOutline
{
    // Source locations are offsets into the original UTF-16 document (including CRLF).
    public static IReadOnlyList<NoteHeading> Parse(string text)
    {
        var result = new List<NoteHeading>();
        var lines = Regex.Matches(text, @"[^\r\n]*(?:\r\n|\n|\r|$)").Cast<Match>().Where(m => m.Length > 0).ToArray();
        char fence = '\0'; int fenceLength = 0;
        string? previous = null; int previousLine = 0, previousOffset = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            var raw = lines[index].Value.TrimEnd('\r', '\n');
            var line = Regex.Replace(raw, @"^(?: {0,3}>[ \t]?)+", "");
            var fenceLine = Regex.Replace(line, @"^ {0,3}(?:[-+*]|\d+[.)])[ \t]+", "");
            var fenced = Regex.Match(fenceLine, @"^ {0,3}(`{3,}|~{3,})(.*)$");
            if (fence != '\0')
            {
                if (fenced.Success && fenced.Groups[1].Value[0] == fence && fenced.Groups[1].Length >= fenceLength && string.IsNullOrWhiteSpace(fenced.Groups[2].Value)) fence = '\0';
                previous = null; continue;
            }
            if (fenced.Success && (fenced.Groups[1].Value[0] != '`' || !fenced.Groups[2].Value.Contains('`')))
            { fence = fenced.Groups[1].Value[0]; fenceLength = fenced.Groups[1].Length; previous = null; continue; }
            if (line.StartsWith("    ") || line.StartsWith('\t')) { previous = null; continue; }
            var atx = Regex.Match(fenceLine, @"^ {0,3}(#{1,6})(?:[ \t]+(.*)|[ \t]*)$");
            if (atx.Success)
            {
                var heading = Regex.Replace(atx.Groups[2].Value, @"[ \t]+#+[ \t]*$", "");
                result.Add(new("source-" + lines[index].Index, PlainText(heading), atx.Groups[1].Length, index + 1, lines[index].Index));
                previous = null; continue;
            }
            var underline = Regex.Match(line, @"^ {0,3}(=+|-+)[ \t]*$");
            if (underline.Success && previous is not null)
            {
                result.Add(new("source-" + previousOffset, PlainText(previous), underline.Groups[1].Value[0] == '=' ? 1 : 2, previousLine, previousOffset));
                previous = null; continue;
            }
            // A list marker or table rule is not a setext heading's preceding paragraph.
            if (string.IsNullOrWhiteSpace(line) || Regex.IsMatch(line, @"^\s*(?:[-+*]\s|\d+[.)]\s|[|]|(?:[-*_]\s*){3,}$)")) previous = null;
            else if (previous is not null) previous += " " + line;
            else { previous = line; previousLine = index + 1; previousOffset = lines[index].Index; }
        }
        return result;
    }
    private static string PlainText(string text)
    {
        var escapes = new List<string>();
        text = Regex.Replace(text, @"\\([!""#$%&'()*+,\-./:;<=>?@\[\]\\^_`{|}~])", m => { escapes.Add(m.Groups[1].Value); return "\uE000" + (escapes.Count - 1) + "\uE001"; });
        text = Regex.Replace(text, @"!?\[([^\]]*)\]\([^)]*\)", "$1");
        text = Regex.Replace(text, @"!?\[([^\]]*)\]\[[^\]]*\]", "$1");
        text = Regex.Replace(text, @"<[^>]+>", "");
        text = Regex.Replace(text, @"(`+)(.*?)\1", "$2");
        text = Regex.Replace(text, @"(\*\*|__|~~)(.+?)\1", "$2");
        text = Regex.Replace(text, @"(?<!\w)([*_])(.+?)\1(?!\w)", "$2");
        text = Regex.Replace(text, "\uE000(\\d+)\uE001", m => escapes[int.Parse(m.Groups[1].Value)]);
        text = WebUtility.HtmlDecode(text).Trim();
        return text.Length == 0 ? "未命名标题" : text;
    }
}
