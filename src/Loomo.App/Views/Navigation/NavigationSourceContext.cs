using System.IO;
using System.Linq;
using System.Text;

namespace sk0ya.Loomo.App.Views;

/// <summary>ナビゲーション候補の周辺ソースをpeek表示用に切り出す。</summary>
public static class NavigationSourceContext
{
    public static string Read(string filePath, int line, int radius = 2, int maxLineCharacters = 240)
    {
        if (string.IsNullOrWhiteSpace(filePath) || line < 0) return "";
        radius = Math.Clamp(radius, 0, 20);
        maxLineCharacters = Math.Clamp(maxLineCharacters, 40, 2000);

        try
        {
            var start = Math.Max(0, line - radius);
            var lines = File.ReadLines(filePath).Skip(start).Take(radius * 2 + 1).ToArray();
            if (lines.Length == 0) return "";

            var builder = new StringBuilder();
            for (var index = 0; index < lines.Length; index++)
            {
                var actualLine = start + index;
                var text = lines[index].TrimEnd('\r', '\n');
                if (text.Length > maxLineCharacters)
                    text = text[..maxLineCharacters] + "…";
                builder.Append(actualLine == line ? "▶" : " ")
                    .Append($" {actualLine + 1,4}  ")
                    .Append(text)
                    .Append('\n');
            }
            return builder.ToString().TrimEnd();
        }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }
}
