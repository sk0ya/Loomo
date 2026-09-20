using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Configuration;

/// <summary>
/// 診断の文面に<b>理由</b>を足す。言語サーバーが言うのは結論だけのことがあり、
/// 「Using ディレクティブは必要ありません」は正しいのに、<b>なぜ</b>不要なのかは書かれていない
/// ——実際、使っている名前空間なのに不要と言われて誤検知に見えた、という行き違いが起きた
/// （答えは <c>GlobalUsings.cs</c> の <c>global using</c> と重複しているから）。
///
/// <para>理由を知っているのはプロジェクトを抱えている側なので、ホストである Loomo が答える。
/// 規則ごとの説明はここの1枚の表に集め、増やすときはここへ足す
/// （<see cref="Explain"/> の switch）。答えられない規則には何も言わない——
/// 曖昧な一般論を足すと、本当に説明がある診断と見分けが付かなくなる。</para>
/// </summary>
public sealed class CSharpDiagnosticExplanationService
{
    /// <summary>global using の索引を作り直す間隔。ホバーのたびにプロジェクト全体を読み直さない。</summary>
    private static readonly TimeSpan IndexLifetime = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, GlobalUsingIndex> _indexes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><paramref name="diagnostic"/> に添える1行。説明できないなら null。</summary>
    public string? Explain(
        SolutionModel? solution, string filePath, string source, LspDiagnostic diagnostic)
    {
        if (diagnostic.Code is not { Length: > 0 } code) return null;

        return code.ToUpperInvariant() switch
        {
            // 不要な using。IDE0005（Roslyn の IDE 規則）と CS8019（コンパイラ）が同じことを言う。
            "IDE0005" or "CS8019" => ExplainUnnecessaryUsings(solution, filePath, source, diagnostic.Range),
            _ => null,
        };
    }

    /// <summary>
    /// 不要と言われた using を<b>1本ずつ</b>見分ける。Roslyn は連続する不要 using を1件の広い範囲で
    /// 返すので、範囲を行へ切り直さないと「どの行のことか」が誰にも分からない。
    /// </summary>
    private string? ExplainUnnecessaryUsings(
        SolutionModel? solution, string filePath, string source, LspRange range)
    {
        var lines = source.Split('\n');
        int first = Math.Max(0, range.Start.Line);
        int last = Math.Min(lines.Length - 1, range.End.Line);
        if (first > last) return null;

        var directives = new List<(int Line, string Namespace, bool Static)>();
        for (int i = first; i <= last; i++)
        {
            var match = UsingDirective.Match(lines[i].TrimEnd('\r'));
            if (!match.Success) continue;
            directives.Add((i + 1, Normalize(match.Groups["ns"].Value), match.Groups["static"].Success));
        }
        if (directives.Count == 0) return null;

        var index = GetGlobalUsingIndex(solution, filePath);
        var duplicates = new List<(int Line, GlobalUsing Global)>();
        var unused = new List<(int Line, string Namespace)>();
        foreach (var (line, ns, isStatic) in directives)
        {
            if (index.TryGet(ns, isStatic, out var global)) duplicates.Add((line, global));
            else unused.Add((line, ns));
        }

        return Compose(duplicates, unused);
    }

    private static string? Compose(
        IReadOnlyList<(int Line, GlobalUsing Global)> duplicates,
        IReadOnlyList<(int Line, string Namespace)> unused)
    {
        if (duplicates.Count == 0 && unused.Count == 0) return null;

        var text = new StringBuilder();
        if (duplicates.Count == 1 && unused.Count == 0)
        {
            var (line, global) = duplicates[0];
            text.Append($"{line}行目は {global.Describe()} と重複しています（消しても解決は変わりません）。");
            return text.ToString();
        }
        if (duplicates.Count == 0 && unused.Count == 1)
            return $"{unused[0].Line}行目（{unused[0].Namespace}）は、この本文で使われていません。";

        text.Append($"対象は using {duplicates.Count + unused.Count} 件。");
        if (duplicates.Count > 0)
            text.Append($"{duplicates.Count} 件は {duplicates[0].Global.Describe()} と重複。");
        if (unused.Count > 0)
            text.Append($"{DescribeLines(unused)} はこの本文で使われていません。");
        return text.ToString();
    }

    /// <summary>行番号は<b>数えるのではなく挙げる</b>——「どの行か」が知りたいことなので。
    /// 多いときだけ先頭3つに切り、残りは件数で示す。</summary>
    private static string DescribeLines(IReadOnlyList<(int Line, string Namespace)> items)
    {
        var shown = string.Join("・", items.Take(3).Select(item => $"{item.Line}行目"));
        return items.Count <= 3 ? shown : $"{shown} ほか計{items.Count}件";
    }

    private GlobalUsingIndex GetGlobalUsingIndex(SolutionModel? solution, string filePath)
    {
        var project = solution?.ProjectForFile(filePath);
        if (project?.SelectedTargetFrameworkModel is not { } target) return GlobalUsingIndex.Empty;

        if (_indexes.TryGetValue(project.FullPath, out var cached) && !cached.IsStale)
            return cached;

        var index = GlobalUsingIndex.Build(target.CompileFiles.Select(file => file.FullPath));
        _indexes[project.FullPath] = index;
        return index;
    }

    /// <summary>`using System.Windows;` / `using static X.Y;`。別名付き（`using A = B;`）は別物なので拾わない。</summary>
    private static readonly Regex UsingDirective = new(
        @"^\s*using\s+(?<static>static\s+)?(?<ns>(?:global::)?[A-Za-z_@][\w.@]*)\s*;\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>`global::` 付きは同じ名前空間を指す綴り違い（ビルドが生成する global using は
    /// 必ずこの形）。突き合わせる前に外す。</summary>
    private static string Normalize(string ns) =>
        ns.StartsWith("global::", StringComparison.Ordinal) ? ns["global::".Length..] : ns;

    /// <summary>`global using ...;` の宣言1件。</summary>
    private sealed record GlobalUsing(string Namespace, bool Static, string FilePath, int Line)
    {
        /// <summary>人に見せる綴り。ビルドが生成した global using（<c>ImplicitUsings</c>）は
        /// 中間出力の中にあって開いても仕方がないので、そう言う。</summary>
        public string Describe()
        {
            var name = Path.GetFileName(FilePath);
            return IsGenerated
                ? $"ImplicitUsings が生成した global using（{name}:{Line}）"
                : $"{name}:{Line} の global using";
        }

        private bool IsGenerated =>
            FilePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase) ||
            FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>プロジェクト1つぶんの global using の索引。テキスト走査だけで作る——
    /// ここで Compilation を組むと、ホバー1回が数秒になる。</summary>
    private sealed class GlobalUsingIndex
    {
        public static readonly GlobalUsingIndex Empty =
            new(new Dictionary<string, GlobalUsing>(StringComparer.Ordinal), DateTime.MaxValue);

        private readonly IReadOnlyDictionary<string, GlobalUsing> _byName;
        private readonly DateTime _expires;

        private GlobalUsingIndex(IReadOnlyDictionary<string, GlobalUsing> byName, DateTime expires)
        {
            _byName = byName;
            _expires = expires;
        }

        public bool IsStale => DateTime.UtcNow >= _expires;

        public bool TryGet(string ns, bool isStatic, out GlobalUsing global) =>
            _byName.TryGetValue(Key(ns, isStatic), out global!);

        public static GlobalUsingIndex Build(IEnumerable<string> compileFiles)
        {
            var map = new Dictionary<string, GlobalUsing>(StringComparer.Ordinal);
            foreach (var path in compileFiles)
            {
                string[] lines;
                try
                {
                    // global using はファイル先頭にしか書けない。全文を読まずに頭だけ見る。
                    lines = ReadHead(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    continue;
                }

                for (int i = 0; i < lines.Length; i++)
                {
                    var match = GlobalUsingDirective.Match(lines[i]);
                    if (!match.Success) continue;
                    var ns = Normalize(match.Groups["ns"].Value);
                    var isStatic = match.Groups["static"].Success;
                    map.TryAdd(Key(ns, isStatic), new GlobalUsing(ns, isStatic, path, i + 1));
                }
            }
            return new GlobalUsingIndex(map, DateTime.UtcNow + IndexLifetime);
        }

        /// <summary>先頭の何行かだけ読む。global using はどのファイルにも書けるが、
        /// 必ず名前空間・型宣言より<b>前</b>にある。</summary>
        private static string[] ReadHead(string path, int maxLines = 200)
        {
            var lines = new List<string>(Math.Min(maxLines, 64));
            using var reader = new StreamReader(path);
            for (int i = 0; i < maxLines; i++)
            {
                var line = reader.ReadLine();
                if (line is null) break;
                lines.Add(line);
            }
            return [.. lines];
        }

        private static string Key(string ns, bool isStatic) => isStatic ? "static " + ns : ns;

        private static readonly Regex GlobalUsingDirective = new(
            @"^\s*global\s+using\s+(?<static>static\s+)?(?<ns>(?:global::)?[A-Za-z_@][\w.@]*)\s*;\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }
}
