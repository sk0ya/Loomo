using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services;

/// <summary>reflog（ref が指す先を動かした操作の記録）を読む。書き換えは一切しない。</summary>
public sealed class GitReflogService
{
    private readonly GitCommandRunner _runner;

    public GitReflogService(GitCommandRunner runner) => _runner = runner;

    /// <summary>
    /// <paramref name="refName"/> の reflog を新しい順に <paramref name="skip"/> 件飛ばして <paramref name="take"/> 件。
    /// 1件多く頼むのは、ページ末尾の記録にも「操作の前」（＝次の記録）を埋めるためと、続きの有無を知るため。
    /// 失敗は理由付きで返す——空一覧だけ返すと「記録が無い」と取り違える。
    /// </summary>
    public async Task<GitReflogPage> GetPageAsync(string refName, int skip, int take)
    {
        var args = new List<string>
        {
            "log", "-g", "--date=iso-strict", "--format=" + GitReflogParser.Format,
            $"--max-count={take + 1}",
        };
        if (skip > 0) args.Add($"--skip={skip}");
        // 末尾の "--" は必須：ref と同名のファイルがあると git が曖昧な引数として拒む。
        args.Add(refName);
        args.Add("--");

        var result = await _runner.RunAsync(args.ToArray()).ConfigureAwait(false);
        if (result.Success)
            return GitReflogParser.Parse(result.Output, refName, skip, take);

        // まだ1つもコミットの無いリポジトリや、reflog を持たない ref は git が失敗で答える。
        // それは「記録が無い」であって壊れているのではないので、空として返す。
        var message = result.Message.Trim();
        if (message.Contains("does not have any commits", StringComparison.OrdinalIgnoreCase)
            || message.Contains("no reflog", StringComparison.OrdinalIgnoreCase)
            || message.Contains("bad revision", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unknown revision", StringComparison.OrdinalIgnoreCase))
            return GitReflogPage.Empty;
        return new GitReflogPage(Array.Empty<GitReflogEntry>(), false, message);
    }

    /// <summary>
    /// 渡したコミットのうち、<b>どのブランチ・タグ・リモート追跡ブランチ・いまの HEAD からも辿れない</b>もの。
    /// reflog を開く一番の理由は「消えたコミットを取り戻す」ことなので、それがどの行かを一覧で見せるのに使う。
    /// <c>rev-list A B … --not --branches --tags --remotes HEAD</c> は「A・B… からは辿れるが ref からは辿れない」
    /// コミットだけを列挙する＝歩くのは消えかけの部分だけで、履歴全体は歩かない。
    /// 失敗したら空（＝印を付けない）。印が出ないのは困らないが、間違った印は消えていないものを消えたと言う。
    /// </summary>
    public async Task<IReadOnlySet<string>> GetUnreachableAsync(IEnumerable<string> hashes)
    {
        var candidates = hashes.Where(h => !string.IsNullOrEmpty(h))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (candidates.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var args = new List<string> { "rev-list" };
        args.AddRange(candidates);
        args.AddRange(new[] { "--not", "--branches", "--tags", "--remotes", "HEAD", "--" });
        var result = await _runner.RunAsync(args.ToArray()).ConfigureAwait(false);
        if (!result.Success)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var listed = new HashSet<string>(
            result.Output.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        listed.IntersectWith(candidates);
        return listed;
    }
}
