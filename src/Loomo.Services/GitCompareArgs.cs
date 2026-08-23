using System;
using System.Collections.Generic;
using System.Linq;

namespace sk0ya.Loomo.Services;

/// <summary>
/// 比較基準まわりの純粋なロジック（git を起動しない）：既定ブランチの推定と git 引数の組み立て。
/// UI にも <see cref="GitCommandRunner"/> にも依存しないので単体テストできる。
///
/// <para><b>二点記法を使う（三点記法ではない）</b>——<c>git diff &lt;base&gt;</c> は
/// 「base ↔ <b>作業ツリー</b>」で、<c>git diff &lt;base&gt;...HEAD</c> は「分岐点 ↔ <b>HEAD</b>」。
/// 後者は<b>未コミットの変更を含まない</b>。この機能の目的は「このブランチで自分が入れた変更を全部見たい」
/// なので、まだコミットしていない編集も入っていなければ嘘になる。よって
/// 「分岐点と比較」は <c>merge-base</c> を自分で解決してから同じ二点記法で引く
/// （＝<c>base...HEAD</c> ＋ 未コミット分、という意味になる）。</para>
///
/// <para><b>未追跡ファイルは出さない</b>——<c>git diff</c> は未追跡を見ないし、
/// 「base から見てこのブランチが入れた変更」に、まだ git に足していないファイルは含まれない
/// （作業ツリー基準の一覧には従来どおり「バージョン管理外ファイル」として出る）。</para>
///
/// <para><b>リネームは1件にまとめる</b>——<c>--find-renames</c> を付け、<c>R100\told\tnew</c> は
/// 旧パス付きの1エントリ（削除＋追加の2行ではない）として扱う。差分もその両方のパスを渡して引く。</para>
/// </summary>
public static class GitCompareArgs
{
    /// <summary>
    /// pathspec をグロブとして解釈させない git のトップレベルオプション。git の pathspec は既定で
    /// ワイルドカードが効くので、これが無いと <c>a[1].txt</c> の差分に <c>a1.txt</c> が混ざる。
    /// ここで渡すのは実在するファイルパスそのものなので、常にリテラルでよい。
    /// </summary>
    public const string LiteralPathspecs = "--literal-pathspecs";

    /// <summary>既定ブランチの候補（<c>origin/HEAD</c> が無いリポジトリでの探索順）。</summary>
    public static readonly IReadOnlyList<string> DefaultBranchCandidates =
        new[] { "main", "master", "origin/main", "origin/master" };

    /// <summary>
    /// 既定ブランチを推定する。<paramref name="originHeadRef"/>（<c>git symbolic-ref refs/remotes/origin/HEAD</c>
    /// の出力。例 <c>refs/remotes/origin/main</c>）を第一候補にし、無ければ
    /// <see cref="DefaultBranchCandidates"/> の順に <paramref name="availableRefs"/> の中から選ぶ。
    /// リモート追跡が無いリポジトリでも壊れないよう、<paramref name="originHeadRef"/> は null 可。
    /// どれも無ければ null（呼び出し側は「ブランチを選んでください」を出す）。
    /// </summary>
    public static string? PickDefaultBranch(string? originHeadRef, IEnumerable<string> availableRefs)
    {
        var refs = new HashSet<string>(
            availableRefs?.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim())
                ?? Enumerable.Empty<string>(),
            StringComparer.Ordinal);

        if (ShortenRemoteHead(originHeadRef) is { } fromOriginHead
            && (refs.Count == 0 || refs.Contains(fromOriginHead)))
            return fromOriginHead;

        return DefaultBranchCandidates.FirstOrDefault(refs.Contains);
    }

    /// <summary><c>refs/remotes/origin/main</c> → <c>origin/main</c>。それ以外の形は null。</summary>
    public static string? ShortenRemoteHead(string? symbolicRef)
    {
        var value = symbolicRef?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        const string prefix = "refs/remotes/";
        if (!value.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var name = value[prefix.Length..];
        return name.Length == 0 || name.EndsWith("/HEAD", StringComparison.Ordinal) ? null : name;
    }

    /// <summary>
    /// 基準に対する変更ファイル一覧を引く引数
    /// （<c>git diff --name-status --find-renames &lt;base&gt; --</c>）。二点記法なので未コミットの編集も出る。
    ///
    /// <para><b>末尾の <c>--</c> は必須</b>——git は「revision かパスか」が曖昧な引数を拒む。
    /// <c>docs</c>／<c>main</c>／<c>test</c> のようにブランチ名と同名のディレクトリが直下にあると
    /// <c>fatal: ambiguous argument</c> で終了コード 128 になり、付けないと一覧が空＝
    /// 「差分があるのに無い」と嘘をつくことになる。</para>
    /// </summary>
    public static string[] NameStatusArgs(string baseRef)
        => new[]
        {
            "--no-optional-locks", LiteralPathspecs, "diff", "--name-status", "--find-renames", baseRef, "--",
        };

    /// <summary>基準に対する1ファイルの差分を引く引数。リネームは旧パスも pathspec に含める
    /// （新パスだけだと、名前が変わったファイルの中身の差分が空になる）。</summary>
    public static string[] FileDiffArgs(string baseRef, GitCommitFileChange file, int contextLines)
    {
        var args = new List<string>
        {
            "--no-optional-locks", LiteralPathspecs, "diff", $"--unified={contextLines}",
            "--find-renames", baseRef, "--",
        };
        if (file.OrigPath is not null)
            args.Add(file.OrigPath);
        args.Add(file.Path);
        return args.ToArray();
    }

    /// <summary>基準ブランチと HEAD の分岐点を引く引数。</summary>
    public static string[] MergeBaseArgs(string branch) => new[] { "merge-base", branch, "HEAD" };

    /// <summary>ref がコミットとして存在するかを確かめる引数（存在しなければ終了コード非0・出力なし）。</summary>
    public static string[] VerifyCommitArgs(string reference)
        => new[] { "rev-parse", "--verify", "--quiet", $"{reference}^{{commit}}" };
}
