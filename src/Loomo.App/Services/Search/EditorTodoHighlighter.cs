using System.IO;
using Editor.Core.Syntax;

namespace sk0ya.Loomo.App.Services;

/// <summary>エディタの見えている行から TODO のタグを探して強調範囲を返す。何を TODO とするか・どの拡張子を
/// 見るかは TODO Tree と同じ（<see cref="TodoTagSyntax"/>・TODO Tree の検索設定）。</summary>
public sealed class EditorTodoHighlighter(Func<TodoTreeSettings> settings)
{
    // 1 行だけ極端に長いファイル（圧縮済みの JS など）で UI スレッドを止めない。
    private const int MaxLineLength = 2000;
    private static readonly IReadOnlyDictionary<int, IReadOnlyList<LineHighlightSpan>> None =
        new Dictionary<int, IReadOnlyList<LineHighlightSpan>>();

    private string? _codeSource, _documentSource;
    private HashSet<string> _code = [], _documents = [];

    public IReadOnlyDictionary<int, IReadOnlyList<LineHighlightSpan>> Highlight(
        string? filePath, string[] lines, LineTokens[] tokens, int firstLine, int lastLine)
    {
        if (string.IsNullOrEmpty(filePath) || lines.Length == 0) return None;
        RefreshExtensions();
        var extension = Path.GetExtension(filePath);
        if (_code.Contains(extension)) return InCode(lines, tokens, firstLine, lastLine);
        return _documents.Contains(extension) ? InDocument(lines, firstLine, lastLine) : None;
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<LineHighlightSpan>> InCode(
        string[] lines, LineTokens[] tokens, int firstLine, int lastLine)
    {
        if (tokens.Length == 0) return None;
        Dictionary<int, IReadOnlyList<LineHighlightSpan>>? result = null;
        // 字句はファイル全体か見えている範囲のどちらかで、どちらも行番号の順に連続している。
        var offset = tokens[0].Line;
        for (var line = firstLine; line <= lastLine && line < lines.Length; line++)
        {
            var index = line - offset;
            if (index < 0 || index >= tokens.Length || tokens[index].Line != line) continue;
            var text = lines[line];
            if (!Candidate(text)) continue;
            try
            {
                var spans = TodoTagSyntax.FindInCode(text, tokens[index].Tokens)
                    .Select(hit => new LineHighlightSpan(hit.Match.Index, hit.Match.Length)).ToList();
                if (spans.Count > 0) (result ??= [])[line] = spans;
            }
            catch (RegexMatchTimeoutException) { }
        }
        return result ?? None;
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<LineHighlightSpan>> InDocument(
        string[] lines, int firstLine, int lastLine)
    {
        Dictionary<int, IReadOnlyList<LineHighlightSpan>>? result = null;
        var inFence = false;
        // コードフェンスの中かは先頭から数えないと分からない。
        for (var line = 0; line <= lastLine && line < lines.Length; line++)
        {
            var text = lines[line];
            if (MayBeFence(text) && TodoTagSyntax.IsFence(text)) { inFence = !inFence; continue; }
            if (line < firstLine || inFence || !Candidate(text)) continue;
            try
            {
                if (TodoTagSyntax.Find(text) is { } match)
                    (result ??= [])[line] = [new LineHighlightSpan(match.Index, match.Length)];
            }
            catch (RegexMatchTimeoutException) { }
        }
        return result ?? None;
    }

    private static bool Candidate(string text) => text.Length is > 0 and <= MaxLineLength && text.Contains(':') | text.Contains('：');

    private static bool MayBeFence(string text)
    {
        var i = 0;
        while (i < 3 && i < text.Length && text[i] == ' ') i++;
        return i < text.Length && text[i] is '`' or '~';
    }

    private void RefreshExtensions()
    {
        var current = settings();
        if (!ReferenceEquals(_codeSource, current.CodeExtensions))
        {
            _codeSource = current.CodeExtensions;
            _code = Parse(_codeSource);
        }
        if (!ReferenceEquals(_documentSource, current.DocumentExtensions))
        {
            _documentSource = current.DocumentExtensions;
            _documents = Parse(_documentSource);
        }
    }

    private static HashSet<string> Parse(string value)
    {
        try { return TodoSearchQuery.ParseExtensions(value).ToHashSet(StringComparer.OrdinalIgnoreCase); }
        catch (ArgumentException) { return []; }
    }
}
