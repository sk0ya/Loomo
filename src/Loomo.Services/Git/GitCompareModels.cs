using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.Services;

/// <summary>Git の変更一覧・差分を「何に対する差分」として見るか（比較基準）。</summary>
public enum GitCompareBaseKind
{
    /// <summary>作業ツリー（HEAD／インデックス）。既定であり、これまでどおりの見え方。</summary>
    WorkingTree,

    /// <summary>選んだブランチとの比較（<c>git diff &lt;branch&gt;</c>）。</summary>
    Branch,

    /// <summary>選んだブランチと HEAD の分岐点との比較
    /// （<c>git merge-base &lt;branch&gt; HEAD</c> を解決してから <c>git diff &lt;mergeBase&gt;</c>）。
    /// ＝「このブランチで自分が入れた変更だけ」。</summary>
    MergeBase,

    /// <summary>別の作業ツリー（<c>git worktree</c>）の<b>いまの状態</b>との比較。コミット済みだけでなく
    /// 向こうの未コミットの編集・未追跡ファイルも含む（<see cref="GitWorktreeService.SnapshotAsync"/> で
    /// tree に固めてから <c>git diff &lt;tree&gt;</c>）。対象は作業ツリーのパス。</summary>
    Worktree,

    /// <summary>任意のリビジョン（タグ・コミットハッシュ・<c>HEAD~3</c> など）との比較。
    /// ブランチ一覧に無いものを基準にしたいとき（コミット一覧の「作業ツリーと比較」もこれ）。</summary>
    Revision,
}

/// <summary>
/// 比較基準の選択そのもの（種別＋対象）。UI にもワークスペース状態にもこの形で載る。
/// <see cref="Target"/> の意味は種別で決まる：ブランチ／分岐点ではブランチ名、作業ツリー比較では
/// 作業ツリーのパス、リビジョン比較ではリビジョン（ハッシュ・タグ等）。作業ツリー基準では null。
/// </summary>
public sealed record GitCompareBaseSelection(GitCompareBaseKind Kind, string? Target)
{
    /// <summary>既定＝作業ツリー。</summary>
    public static readonly GitCompareBaseSelection WorkingTree =
        new(GitCompareBaseKind.WorkingTree, null);

    /// <summary>作業ツリー基準か（＝ステージ・破棄など index/HEAD 概念の操作が意味を持つか）。</summary>
    public bool IsWorkingTree => Kind == GitCompareBaseKind.WorkingTree;

    /// <summary>ブランチ名を要する種別か（ブランチ選択 UI を出すかの判定）。</summary>
    public bool NeedsBranch => Kind is GitCompareBaseKind.Branch or GitCompareBaseKind.MergeBase;

    /// <summary>対象（ブランチ・作業ツリー・リビジョン）を要する種別か。</summary>
    public bool NeedsTarget => Kind != GitCompareBaseKind.WorkingTree;

    /// <summary>結果が時間とともに変わり得るか（キャッシュしてはいけないか）。作業ツリー比較の相手は
    /// <b>こちらのリポジトリ監視に現れない場所</b>で編集されるので、解決は毎回やり直す。</summary>
    public bool IsVolatile => Kind == GitCompareBaseKind.Worktree;
}

/// <summary>
/// 比較基準の解決結果。<see cref="BaseRef"/> が null かつ <see cref="Error"/> も null なら作業ツリー基準。
/// <see cref="Error"/> が非 null なら基準を解決できなかった（空リポジトリ・ブランチ不在・分岐点なし）——
/// 一覧・差分は空にし、この理由をそのまま画面に出す（黙って作業ツリーへ落とさない）。
/// </summary>
public sealed record GitCompareResolution(string? BaseRef, string? Error, string Label)
{
    public static readonly GitCompareResolution WorkingTree = new(null, null, "作業ツリー");

    public bool HasError => Error is not null;

    /// <summary>ブランチ／分岐点基準として解決できたか。</summary>
    public bool IsBaseComparison => BaseRef is not null;
}

/// <summary>
/// 基準に対する変更ファイル一覧の取得結果。<b>失敗を握りつぶさない</b>ための器——
/// 空リストだけを返すと「差分があるのに変更なし」と画面が嘘をつく（曖昧な引数・壊れた ref など）。
/// </summary>
public sealed record GitCompareChanges(IReadOnlyList<GitCommitFileChange> Files, string? Error)
{
    public static readonly GitCompareChanges Empty =
        new(Array.Empty<GitCommitFileChange>(), null);

    public bool HasError => Error is not null;
}

/// <summary>
/// 比較基準に対する変更ファイル1件と、その差分を引くための基準 ref。
/// 一覧の項目から差分本体を引くとき、<b>その項目が作られたときの基準</b>で引けるようにするための組
/// （基準を切り替えた直後に古い ref で差分を引いてしまう取り違えを型で防ぐ）。
/// </summary>
public sealed record GitCompareFile(string BaseRef, GitCommitFileChange Change);

/// <summary>
/// その比較基準で<b>意味を持つ操作</b>。ステージ／アンステージ／破棄／行・範囲単位の適用は
/// 「作業ツリー vs インデックス／HEAD」の概念で、<c>main</c> との比較には存在しない——
/// 基準がブランチ／分岐点のときは<b>出さない</b>（無効化して押せるのに何も起きない項目にしない）。
/// ファイルを開く・エディタへ送る・履歴を見るは基準に依らないので、ここには現れない（常に可）。
/// </summary>
public sealed record GitCompareCapabilities(
    bool CanStage, bool CanUnstage, bool CanDiscard, bool CanApplyLines, bool CanCommit)
{
    public static GitCompareCapabilities For(GitCompareBaseKind kind)
        => kind == GitCompareBaseKind.WorkingTree
            ? new GitCompareCapabilities(true, true, true, true, true)
            : new GitCompareCapabilities(false, false, false, false, false);

    public static GitCompareCapabilities For(GitCompareBaseSelection selection) => For(selection.Kind);
}

/// <summary>2点比較の片端。ref（ブランチ・タグ・ハッシュ）か、作業ツリー（いまの状態）。</summary>
public sealed record GitCompareEndpoint(GitCompareEndpointKind Kind, string Value, string Label)
{
    public static GitCompareEndpoint Ref(string reference) => new(GitCompareEndpointKind.Ref, reference, reference);

    public static GitCompareEndpoint Worktree(GitWorktreeInfo worktree)
        => new(GitCompareEndpointKind.Worktree, worktree.Path, $"{worktree.DisplayName}（作業中）");
}

public enum GitCompareEndpointKind
{
    Ref,
    Worktree,
}

/// <summary>
/// 2点比較を解決した結果。<see cref="FromRef"/> と <see cref="ToRef"/> はどちらも <c>git diff</c> にそのまま
/// 渡せるもの（コミットか tree のハッシュ・ref 名）。失敗なら <see cref="Error"/> に日本語の理由。
/// </summary>
public sealed record GitCompareRange(string? FromRef, string? ToRef, string Label, string? Error)
{
    public bool HasError => Error is not null;
}
