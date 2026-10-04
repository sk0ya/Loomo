using Editor.Core.Syntax;

namespace sk0ya.Loomo.App.Services;

public sealed record TodoSearchOptions(string CodeExtensions, string DocumentExtensions, string? ExcludeGlob, int MaxResults);
public sealed record TodoSearchResult(IReadOnlyList<TodoEntry> Entries, bool Truncated = false, string? Notice = null);
public interface ITodoSearchService
{
    Task<TodoSearchResult> SearchAsync(TodoSearchOptions options, CancellationToken ct);
}

/// <summary>拡張子で候補を限定し、コードは文書全体の字句解析からコメント内のタグだけを採用する。</summary>
public sealed class TodoSearchQuery(IWorkspaceSearchService search, IWorkspaceService workspace) : ITodoSearchService
{
    private const int CandidateLimit = 20000;
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private static readonly Regex Tags = new(TodoTreeViewModel.TagPattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static SyntaxEngine CreateEngine()
    {
        var registry = SyntaxLanguageRegistry.CreateDefault();
        sk0ya.Loomo.CSharp.Editor.CSharpEditorIntegration.ConfigureSyntax(registry);
        return new SyntaxEngine(registry);
    }

    public static string[] ParseExtensions(string value)
    {
        return Regex.Split(value.Trim(), @"[\s,;、]+")
            .Where(s => s.Length > 0).Select(s => {
                var extension = s.StartsWith("*.") ? s[1..] : s.StartsWith('.') ? s : "." + s;
                if (!Regex.IsMatch(extension, @"^\.[a-zA-Z0-9_+-]+$"))
                    throw new ArgumentException($"拡張子「{s}」を確認してください。例: .cs .js");
                return extension.ToLowerInvariant();
            }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static void Validate(string code, string documents)
    {
        var engine = CreateEngine();
        foreach (var extension in ParseExtensions(code))
        {
            engine.DetectLanguage("file" + extension);
            if (engine.LanguageName is null || (engine.GetCommentPrefix() is null && engine.GetBlockComment() is null))
                throw new ArgumentException($"{extension} のコメント判定には未対応です。文書の本文検索は別欄で指定できます。");
        }
        ParseExtensions(documents);
    }

    public async Task<TodoSearchResult> SearchAsync(TodoSearchOptions options, CancellationToken ct)
    {
        Validate(options.CodeExtensions, options.DocumentExtensions);
        var code = ParseExtensions(options.CodeExtensions).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extensions = code.Concat(ParseExtensions(options.DocumentExtensions)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (extensions.Length == 0) return new([]);
        var candidates = await search.GrepAsync(TodoTreeViewModel.TagPattern,
            new GrepOptions(CaseSensitive: true, UseRegex: true, ExcludeGlob: options.ExcludeGlob,
                MaxResults: CandidateLimit + 1, Extensions: extensions), ct);
        var entries = new List<TodoEntry>();
        var skipped = 0;
        var engine = CreateEngine();
        foreach (var file in candidates.Take(CandidateLimit).GroupBy(h => h.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (!workspace.Contains(file.Key) || !extensions.Contains(Path.GetExtension(file.Key), StringComparer.OrdinalIgnoreCase)) continue;
            try
            {
                if (new FileInfo(file.Key).Length > MaxFileBytes) { skipped++; continue; }
                // 検索と読み取りの間に変更されても、現在の行番号と列で結果を作り直す。
                var lines = await File.ReadAllLinesAsync(file.Key, ct);
                var isCode = code.Contains(Path.GetExtension(file.Key));
                engine.DetectLanguage(file.Key);
                var tokens = isCode ? engine.Tokenize(lines).ToDictionary(l => l.Line, l => l.Tokens) : null;
                for (var line = 0; line < lines.Length; line++)
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (Match match in Tags.Matches(lines[line]))
                    {
                        int? end = null;
                        if (isCode)
                        {
                            if (!tokens!.TryGetValue(line, out var spans)) continue;
                            var comment = spans.FirstOrDefault(t => t.Kind == TokenKind.Comment
                                && t.StartColumn <= match.Index && t.StartColumn + t.Length >= match.Index + match.Length);
                            if (comment.Length == 0) continue;
                            end = comment.StartColumn + comment.Length;
                        }
                        entries.Add(new(match.Value, new(file.Key, file.First().RelativePath, line + 1, match.Index + 1, lines[line]), end));
                        if (entries.Count > options.MaxResults)
                            return new(entries.Take(options.MaxResults).ToArray(), true, Notice());
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return new(entries, false, Notice());

        string? Notice()
        {
            var notices = new List<string>();
            if (candidates.Count > CandidateLimit) notices.Add("候補行の上限に到達・拡張子や除外条件を絞ってください");
            if (skipped > 0) notices.Add($"{skipped} ファイルを省略（2 MB 超過または読取不可）");
            return notices.Count > 0 ? string.Join("・", notices) : null;
        }
    }
}
