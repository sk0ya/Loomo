using Editor.Core.Syntax;
using Editor.Core.Syntax.Languages;

namespace sk0ya.Loomo.CSharp.Editor;

/// <summary>
/// Visual Studio のソリューションファイル（従来の .sln と XML 形式の .slnx）用の
/// 軽量なシンタックスハイライト。
/// </summary>
public sealed class SolutionSyntax : ISyntaxLanguage
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Project", "EndProject", "Global", "EndGlobal", "ProjectSection", "EndProjectSection",
        "GlobalSection", "EndGlobalSection", "Format", "Version", "VisualStudioVersion",
        "MinimumVisualStudioVersion", "ProjectConfigurationPlatforms", "SolutionConfigurationPlatforms",
        "ProjectDependencies", "NestedProjects", "ExtensibilityGlobals", "SolutionGuid",
        "preSolution", "postSolution", "postProject"
    };

    private readonly XmlSyntax _xml = new();

    public string Name => "Solution";
    public string[] Extensions => [".sln", ".slnx"];
    public string? LineCommentPrefix => "#";

    public LineTokens[] Tokenize(string[] lines)
    {
        // .slnx は XML 形式なので、既存の XML ハイライトをそのまま利用する。
        // 先頭の XML 宣言や <Solution> が無い空ファイルは従来形式として扱う。
        if (LooksLikeXmlSolution(lines))
            return _xml.Tokenize(lines);

        var result = new LineTokens[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            var tokens = new List<SyntaxToken>();
            TokenizeClassicLine(lines[i], tokens);
            result[i] = new LineTokens(i, [.. tokens]);
        }
        return result;
    }

    private static bool LooksLikeXmlSolution(string[] lines)
    {
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0) continue;
            return trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<Solution", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<!--", StringComparison.Ordinal);
        }
        return false;
    }

    private static void TokenizeClassicLine(string line, List<SyntaxToken> tokens)
    {
        int i = 0;
        while (i < line.Length)
        {
            if (char.IsWhiteSpace(line[i]))
            {
                i++;
                continue;
            }

            // .sln のコメントは行頭の #。
            if (line[i] == '#')
            {
                tokens.Add(new SyntaxToken(i, line.Length - i, TokenKind.Comment));
                return;
            }

            // GUID は構造上の識別子として 1 トークンで色付けする。
            if (line[i] == '{')
            {
                int end = line.IndexOf('}', i + 1);
                if (end > i && Guid.TryParse(line[(i + 1)..end], out _))
                {
                    tokens.Add(new SyntaxToken(i, end + 1 - i, TokenKind.Type));
                    i = end + 1;
                    continue;
                }
            }

            // プロジェクト名・パス・構成名など。
            if (line[i] == '"')
            {
                int start = i++;
                while (i < line.Length)
                {
                    if (line[i] == '\\' && i + 1 < line.Length)
                    {
                        i += 2;
                        continue;
                    }
                    if (line[i++] == '"') break;
                }
                var value = i > start + 1 && line[i - 1] == '"'
                    ? line[(start + 1)..(i - 1)]
                    : string.Empty;
                tokens.Add(new SyntaxToken(start, i - start,
                    Guid.TryParse(value, out _) ? TokenKind.Type : TokenKind.String));
                continue;
            }

            if (char.IsDigit(line[i]))
            {
                int start = i++;
                while (i < line.Length && (char.IsDigit(line[i]) || line[i] == '.')) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Number));
                continue;
            }

            if (char.IsLetter(line[i]) || line[i] == '_')
            {
                int start = i++;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                var word = line[start..i];
                tokens.Add(new SyntaxToken(start, i - start,
                    Keywords.Contains(word) ? TokenKind.Keyword : TokenKind.Identifier));
                continue;
            }

            if (line[i] is '=' or ',' or '(' or ')' or ':' or '|')
                tokens.Add(new SyntaxToken(i, 1, TokenKind.Operator));
            i++;
        }
    }
}
