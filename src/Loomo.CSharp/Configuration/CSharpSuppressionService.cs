using System.Text.RegularExpressions;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis.Text;

namespace sk0ya.Loomo.CSharp.Configuration;

/// <summary>
/// C# compiler／Analyzer診断を、診断行だけの pragma で抑制するCode Actionを作る。
/// プロジェクト設定や.editorconfigを書き換えず、通常のWorkspaceEdit preview／Undo経路に渡す。
/// </summary>
public static partial class CSharpSuppressionService
{
    private static readonly Regex SuppressibleCode =
        SuppressibleCodePattern();

    public static IReadOnlyList<LspCodeAction> Get(
        string filePath,
        string source,
        LspDiagnostic diagnostic)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(source) ||
            string.IsNullOrWhiteSpace(diagnostic.Code) ||
            !SuppressibleCode.IsMatch(diagnostic.Code.Trim()))
            return [];

        var text = SourceText.From(source);
        var lineNumber = Math.Clamp(diagnostic.Range.Start.Line, 0, Math.Max(0, text.Lines.Count - 1));
        var line = text.Lines[lineNumber];
        var lineText = line.ToString();
        var trimmed = lineText.TrimStart();
        if (trimmed.StartsWith("#pragma", StringComparison.Ordinal) ||
            trimmed.StartsWith("#if", StringComparison.Ordinal) ||
            trimmed.StartsWith("#elif", StringComparison.Ordinal) ||
            trimmed.StartsWith("#else", StringComparison.Ordinal) ||
            trimmed.StartsWith("#endif", StringComparison.Ordinal))
            return [];

        var code = diagnostic.Code.Trim();
        var lineBreak = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var indentation = lineText[..(lineText.Length - trimmed.Length)];
        var disable = $"{indentation}#pragma warning disable {code}{lineBreak}";
        var restore = $"{indentation}#pragma warning restore {code}{lineBreak}";
        var disableRange = ToRange(text, new TextSpan(line.Start, 0));

        // Include the line break in the insertion point when one exists. At EOF, add a
        // separator explicitly so the restore directive cannot be joined to source code.
        var restorePosition = line.EndIncludingLineBreak;
        var restoreText = restore;
        if (restorePosition == text.Length && !source.EndsWith("\r\n", StringComparison.Ordinal) &&
            !source.EndsWith('\n'))
            restoreText = lineBreak + restore;

        var restoreRange = ToRange(text, new TextSpan(restorePosition, 0));
        var uri = LspUri.FromPath(Path.GetFullPath(filePath));
        var edit = new LspWorkspaceEdit(
            new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.OrdinalIgnoreCase)
            {
                [uri] =
                [
                    new LspTextEdit(disableRange, disable),
                    new LspTextEdit(restoreRange, restoreText),
                ],
            });
        return
        [
            new LspCodeAction(
                $"{code}をこの行で抑制",
                LspCodeActionKinds.QuickFix,
                edit,
                IsPreferred: false),
        ];
    }

    private static LspRange ToRange(SourceText text, TextSpan span)
    {
        var start = text.Lines.GetLinePosition(span.Start);
        var end = text.Lines.GetLinePosition(span.End);
        return new LspRange(
            new LspPosition(start.Line, start.Character),
            new LspPosition(end.Line, end.Character));
    }

    [GeneratedRegex("^(?:CS\\d{4,5}|SA\\d{4})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SuppressibleCodePattern();
}
