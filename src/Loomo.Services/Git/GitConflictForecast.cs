using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services;

/// <summary>
/// いまのブランチを相手（分岐元）へマージしたら衝突するファイル（§24.40）。<see cref="Files"/> が空なら衝突しない。
/// 作業ツリーには一切触らない——判定は git のオブジェクトの上だけで行う（<c>git merge-tree</c>）。
/// </summary>
public sealed record GitConflictForecast(string Target, string HeadOid, string TargetOid, IReadOnlyList<string> Files, string? Error = null)
{
    public bool HasConflicts => Files.Count > 0;
}

/// <summary><c>git merge-tree</c> の出力を読む（純粋・テスト対象）。</summary>
public static class GitMergeTreeParser
{
    /// <summary>
    /// git 2.38 以降の <c>merge-tree --write-tree --name-only --no-messages</c>：1 行目が結果の tree、
    /// 衝突があれば 2 行目以降に衝突したファイル（同じファイルが段ごとに重なることがあるのでまとめる）。
    /// </summary>
    public static IReadOnlyList<string> ParseWriteTreeNames(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// それより古い git の <c>merge-tree &lt;base&gt; &lt;ours&gt; &lt;theirs&gt;</c>（素の 3-way の結果をテキストで出す）。
    /// 「changed in both」「added in both」の節のうち、本文に衝突の印（<c>+&lt;&lt;&lt;&lt;&lt;&lt;&lt; .our</c>）があるものを衝突とみなす。
    /// 片側で消して片側で変えた（modify/delete）のは、この形式では見分けにくいので数えない。
    /// </summary>
    public static IReadOnlyList<string> ParseLegacyConflicts(string output)
    {
        var conflicts = new List<string>();
        string? header = null;
        string? path = null;
        var marked = false;
        void Flush()
        {
            if (marked && path is not null && header is "changed in both" or "added in both" && !conflicts.Contains(path))
                conflicts.Add(path);
            path = null;
            marked = false;
        }
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (!char.IsWhiteSpace(line[0]) && line[0] is not ('@' or '+' or '-'))
            {
                Flush();
                header = line.Trim();
                continue;
            }
            // "  our    100644 <sha> path" ／ "  their  100644 <sha> path"（パスに空白があってもよいように 3 つ目以降を全部）
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("our ", StringComparison.Ordinal) || trimmed.StartsWith("their ", StringComparison.Ordinal)
                || trimmed.StartsWith("base ", StringComparison.Ordinal) || trimmed.StartsWith("result ", StringComparison.Ordinal))
            {
                var parts = trimmed.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 4)
                    path ??= parts[3];
                continue;
            }
            if (line.StartsWith("+<<<<<<< ", StringComparison.Ordinal))
                marked = true;
        }
        Flush();
        return conflicts;
    }
}

/// <summary>衝突の予告を引く（§24.40）。新しい git では <c>--write-tree</c>、古い git では従来の形式で読む。</summary>
public sealed class GitConflictForecastService
{
    private readonly GitCommandRunner _runner;
    private bool? _writeTreeSupported;
    /// <summary>前回の答え。HEAD と相手のコミットが同じなら merge-tree を回し直さない（状態の更新は保存のたびに来る）。</summary>
    private GitConflictForecast? _last;

    public GitConflictForecastService(GitCommandRunner runner) => _runner = runner;

    public async Task<GitConflictForecast> ForecastAsync(string target)
    {
        var head = await RevParseAsync("HEAD").ConfigureAwait(false);
        var other = await RevParseAsync(target).ConfigureAwait(false);
        if (head is null || other is null)
            return new GitConflictForecast(target, head ?? "", other ?? "", Array.Empty<string>(), "ref を解決できません。");
        if (head == other)
            return new GitConflictForecast(target, head, other, Array.Empty<string>());
        if (_last is { } last && last.HeadOid == head && last.TargetOid == other && last.Target == target)
            return last;
        return _last = await ComputeAsync(target, head, other).ConfigureAwait(false);
    }

    private async Task<GitConflictForecast> ComputeAsync(string target, string head, string other)
    {
        if (_writeTreeSupported != false)
        {
            var modern = await _runner.RunAsync(
                "merge-tree", "--write-tree", "--name-only", "--no-messages", head, other).ConfigureAwait(false);
            if (modern.ExitCode is 0 or 1 && modern.Output.Length > 0)
            {
                _writeTreeSupported = true;
                return new GitConflictForecast(target, head, other,
                    modern.ExitCode == 1 ? GitMergeTreeParser.ParseWriteTreeNames(modern.Output) : Array.Empty<string>());
            }
            _writeTreeSupported = false;   // 古い git（使い方の誤りとして返る）。以後は従来の形式だけ使う
        }

        var mergeBase = await _runner.RunAsync("merge-base", head, other).ConfigureAwait(false);
        if (!mergeBase.Success)
            // 共通の祖先が無い（別の歴史）。マージを試す意味が無いので「衝突なし」とは言わず理由を残す。
            return new GitConflictForecast(target, head, other, Array.Empty<string>(), "共通の祖先がありません。");
        var legacy = await _runner.RunAsync("merge-tree", mergeBase.Output.Trim(), head, other).ConfigureAwait(false);
        return legacy.Success
            ? new GitConflictForecast(target, head, other, GitMergeTreeParser.ParseLegacyConflicts(legacy.Output))
            : new GitConflictForecast(target, head, other, Array.Empty<string>(), legacy.Message);
    }

    private async Task<string?> RevParseAsync(string rev)
    {
        var result = await _runner.RunAsync("rev-parse", "--verify", "--quiet", rev + "^{commit}").ConfigureAwait(false);
        return result.Success ? result.Output.Trim() : null;
    }
}

/// <summary>上流から取り込むコミット 1 件（プルの前の確認・§24.64）。</summary>
public sealed record GitIncomingCommit(string ShortHash, string Subject, string Author);
