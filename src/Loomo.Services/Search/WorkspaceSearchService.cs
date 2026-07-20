using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Services.Search;

/// <summary>
/// ファイル名検索・全文検索（grep）の実装。ripgrep（<c>rg</c>）が PATH 上にあればそれを使い
/// （<c>--files</c> / <c>--vimgrep</c>、.gitignore 尊重）、無ければインプロセス走査へ退避する。
/// 検索ルートは既定で <see cref="IWorkspaceService.Folders"/> 全件（マルチルート横断）。呼び出し側が
/// searchRoot を渡せば（いずれかのワークスペースフォルダー配下に限り）そのフォルダへ絞れる
/// （<see cref="ResolveExplicitRoot"/>）。フォルダー未設定なら空を返す。フォルダーごとに
/// <c>WorkingDirectory=そのフォルダー</c>＋検索対象 <c>.</c> でプロセスを起動するため、出力パスは
/// フォルダー相対。複数フォルダー横断時は相対パスの先頭にフォルダー名を付けて区別する
/// （<see cref="FolderPrefix"/>）。<see cref="FileSearchHit.FullPath"/>/<see cref="ContentSearchHit.FullPath"/>
/// は常に絶対パスなのでファイルを開く側はこの表示上の区別に影響されない。
/// </summary>
public sealed class WorkspaceSearchService : IWorkspaceSearchService
{
    private readonly IWorkspaceService _workspace;

    // 走査から除外する重いディレクトリ（rg 不在時のフォールバック用。rg は .gitignore を尊重するため不要）。
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".idea", "packages", "dist", "out",
    };

    private static readonly Lazy<bool> HasRg = new(() => Probe("rg", "--version"));

    public WorkspaceSearchService(IWorkspaceService workspace) => _workspace = workspace;

    public async Task<IReadOnlyList<FileSearchHit>> FindFilesAsync(
        string query, int max, CancellationToken ct, string? searchRoot = null)
    {
        var scopes = ResolveScopes(searchRoot);
        if (scopes.Count == 0)
            return Array.Empty<FileSearchHit>();

        var scored = new List<FileSearchHit>();
        foreach (var (root, prefix) in scopes)
        {
            var relPaths = HasRg.Value
                ? await RunRgAsync(new[] { "--files" }, root, maxLines: 50_000, ct).ConfigureAwait(false)
                : EnumerateRelativeFiles(root).ToList();

            foreach (var raw in relPaths)
            {
                ct.ThrowIfCancellationRequested();
                var rel = NormalizeRel(raw);
                if (rel.Length == 0)
                    continue;

                int score;
                if (string.IsNullOrWhiteSpace(query))
                {
                    score = 0;
                }
                else
                {
                    var name = Path.GetFileName(rel);
                    var byName = FuzzyMatcher.Score(name, query);
                    if (byName is { } s)
                        score = s;
                    else if (FuzzyMatcher.Score(rel, query) is { } sp)
                        score = sp + 5; // パスのみ一致は名前一致より下げる
                    else
                        continue;
                }

                scored.Add(new FileSearchHit(Path.GetFullPath(Path.Combine(root, rel)), WithPrefix(rel, prefix), score));
            }
        }

        return scored
            .OrderBy(h => h.Score)
            .ThenBy(h => h.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    public async Task<IReadOnlyList<ContentSearchHit>> GrepAsync(
        string query, GrepOptions options, CancellationToken ct, string? searchRoot = null)
    {
        if (string.IsNullOrEmpty(query))
            return Array.Empty<ContentSearchHit>();

        var scopes = ResolveScopes(searchRoot);
        if (scopes.Count == 0)
            return Array.Empty<ContentSearchHit>();

        var hits = new List<ContentSearchHit>();
        foreach (var (root, prefix) in scopes)
        {
            var remaining = options.MaxResults - hits.Count;
            if (remaining <= 0)
                break;
            var scoped = options with { MaxResults = remaining };
            hits.AddRange(HasRg.Value
                ? await GrepWithRgAsync(query, scoped, root, prefix, ct).ConfigureAwait(false)
                : GrepInProcess(query, scoped, root, prefix, ct));
        }
        return hits;
    }

    /// <summary>検索対象スコープを決める。<paramref name="searchRoot"/> が空ならワークスペースフォルダー
    /// 全件（プレフィックスは <see cref="Folders"/> が2件以上のときだけフォルダー名を付ける）、指定があれば
    /// いずれかのワークスペースフォルダー配下に限り単一スコープとして採用する（プレフィックス無し）。
    /// どのフォルダーにも属さない・不在なら、ワークスペースフォルダー全体へ退避する。</summary>
    private IReadOnlyList<(string Root, string? Prefix)> ResolveScopes(string? searchRoot)
    {
        var folders = _workspace.Folders.Where(Directory.Exists).ToList();

        if (string.IsNullOrWhiteSpace(searchRoot))
            return folders.Count <= 1
                ? folders.Select(f => (f, (string?)null)).ToList()
                : folders.Select(f => (f, (string?)FolderPrefix(f))).ToList();

        var explicitRoot = ResolveExplicitRoot(searchRoot, folders);
        if (explicitRoot is not null)
            return new[] { (explicitRoot, (string?)null) };

        // ルート外・不在ならワークスペースフォルダー全体へ退避する（既存の単一ルート挙動を踏襲）。
        return folders.Count <= 1
            ? folders.Select(f => (f, (string?)null)).ToList()
            : folders.Select(f => (f, (string?)FolderPrefix(f))).ToList();
    }

    /// <summary>searchRoot（相対はいずれかのワークスペースフォルダー基準、絶対はそのまま）が、実在し、
    /// かついずれかのワークスペースフォルダー配下（フォルダー自身を含む）にあればそのフルパスを返す。
    /// 見つからなければ null（呼び出し側はスコープ無しとして扱う）。</summary>
    private static string? ResolveExplicitRoot(string searchRoot, IReadOnlyList<string> folders)
    {
        foreach (var folder in folders)
        {
            var rootFull = Path.GetFullPath(folder).TrimEnd('\\', '/');
            if (!Directory.Exists(rootFull))
                continue;

            var candidate = Path.GetFullPath(searchRoot, rootFull).TrimEnd('\\', '/');
            if (!Directory.Exists(candidate))
                continue;

            var withinRoot = candidate.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(rootFull + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (withinRoot)
                return candidate;
        }
        return null;
    }

    private static string FolderPrefix(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? folder : name;
    }

    private static string WithPrefix(string relativePath, string? prefix)
        => prefix is null ? relativePath : prefix + "/" + relativePath;

    // ===== ripgrep =====

    private async Task<IReadOnlyList<ContentSearchHit>> GrepWithRgAsync(
        string query, GrepOptions options, string root, string? prefix, CancellationToken ct)
    {
        var args = new List<string> { "--vimgrep", "--color=never" };
        args.Add(options.CaseSensitive ? "-s" : "-i");
        if (!options.UseRegex)
            args.Add("-F"); // 固定文字列（正規表現として解釈しない）
        if (!string.IsNullOrWhiteSpace(options.IncludeGlob))
        {
            args.Add("-g");
            args.Add(options.IncludeGlob);
        }
        if (!string.IsNullOrWhiteSpace(options.ExcludeGlob))
        {
            args.Add("-g");
            args.Add("!" + options.ExcludeGlob);
        }
        args.Add("--"); // 以降をパターン/パスとして扱う（先頭が - のクエリ対策）
        args.Add(query);
        args.Add(".");

        var lines = await RunRgAsync(args, root, maxLines: options.MaxResults, ct).ConfigureAwait(false);
        var hits = new List<ContentSearchHit>(lines.Count);
        foreach (var line in lines)
        {
            if (RgOutputParser.ParseVimgrep(line) is not { } p)
                continue;
            var rel = NormalizeRel(p.Path);
            hits.Add(new ContentSearchHit(
                Path.GetFullPath(Path.Combine(root, rel)), WithPrefix(rel, prefix), p.Line, p.Column, p.Text));
            if (hits.Count >= options.MaxResults)
                break;
        }
        return hits;
    }

    /// <summary>rg を起動し標準出力を行単位で最大 <paramref name="maxLines"/> 行まで読む。
    /// 検索ルートを cwd に固定し、stdin は即閉じる（パス省略時に stdin 待ちで固まるのを防ぐ）。</summary>
    private static async Task<List<string>> RunRgAsync(
        IReadOnlyList<string> args, string workingDir, int maxLines, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("rg")
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        var lines = new List<string>(Math.Min(maxLines, 1024));
        using var process = Process.Start(psi);
        if (process is null)
            return lines;

        using var reg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* 既に終了 */ }
        });

        try { process.StandardInput.Close(); } catch { /* 無視 */ }

        try
        {
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                lines.Add(line);
                if (lines.Count >= maxLines)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* 既に終了 */ }
                    break;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { /* kill 後のパイプ切断は無視 */ }

        try { process.WaitForExit(1000); } catch { /* 無視 */ }
        return lines;
    }

    // ===== インプロセス・フォールバック =====

    private IReadOnlyList<ContentSearchHit> GrepInProcess(
        string query, GrepOptions options, string root, string? prefix, CancellationToken ct)
    {
        Regex? regex = null;
        if (options.UseRegex)
        {
            try
            {
                regex = new Regex(query,
                    options.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            }
            catch (ArgumentException)
            {
                return Array.Empty<ContentSearchHit>(); // 不正な正規表現
            }
        }
        var comparison = options.CaseSensitive
            ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        var include = GlobToRegex(options.IncludeGlob);
        var exclude = GlobToRegex(options.ExcludeGlob);

        var hits = new List<ContentSearchHit>();
        foreach (var rel in EnumerateRelativeFiles(root))
        {
            ct.ThrowIfCancellationRequested();
            if (hits.Count >= options.MaxResults)
                break;
            if (include is not null && !include.IsMatch(rel)) continue;
            if (exclude is not null && exclude.IsMatch(rel)) continue;

            var full = Path.Combine(root, rel);
            string[] fileLines;
            try
            {
                var info = new FileInfo(full);
                if (info.Length > 2_000_000) continue; // 大きすぎる/バイナリ想定はスキップ
                fileLines = File.ReadAllLines(full);
            }
            catch { continue; }

            for (var i = 0; i < fileLines.Length && hits.Count < options.MaxResults; i++)
            {
                var text = fileLines[i];
                int col;
                if (regex is not null)
                {
                    var m = regex.Match(text);
                    if (!m.Success) continue;
                    col = m.Index + 1;
                }
                else
                {
                    var idx = text.IndexOf(query, comparison);
                    if (idx < 0) continue;
                    col = idx + 1;
                }
                hits.Add(new ContentSearchHit(Path.GetFullPath(full), WithPrefix(rel, prefix), i + 1, col, text));
            }
        }
        return hits;
    }

    /// <summary>ルート配下のファイルをルート相対パス（'/' 区切り）で列挙する（重いディレクトリは除外）。</summary>
    private static IEnumerable<string> EnumerateRelativeFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] subDirs;
            string[] files;
            try
            {
                subDirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch { continue; } // アクセス不可は飛ばす

            foreach (var f in files)
                yield return NormalizeRel(Path.GetRelativePath(root, f));

            foreach (var d in subDirs)
            {
                var name = Path.GetFileName(d);
                if (SkipDirs.Contains(name) || name.StartsWith('.'))
                    continue;
                pending.Push(d);
            }
        }
    }

    private static string NormalizeRel(string path)
        => path.Replace('\\', '/').TrimStart('.', '/');

    /// <summary>単純な glob（* と ?）を行頭〜行末アンカーの正規表現へ。null/空は null。</summary>
    private static Regex? GlobToRegex(string? glob)
    {
        if (string.IsNullOrWhiteSpace(glob))
            return null;
        var sb = new StringBuilder("(^|/)");
        foreach (var c in glob)
        {
            sb.Append(c switch
            {
                '*' => "[^/]*",
                '?' => "[^/]",
                _ => Regex.Escape(c.ToString()),
            });
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase);
    }

    private static bool Probe(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return false;
            if (!p.WaitForExit(2000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 既に終了 */ }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
