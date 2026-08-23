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
}

/// <summary>比較基準の選択そのもの（種別＋対象ブランチ）。UI にもワークスペース状態にもこの形で載る。</summary>
public sealed record GitCompareBaseSelection(GitCompareBaseKind Kind, string? Branch)
{
    /// <summary>既定＝作業ツリー。</summary>
    public static readonly GitCompareBaseSelection WorkingTree =
        new(GitCompareBaseKind.WorkingTree, null);

    /// <summary>作業ツリー基準か（＝ステージ・破棄など index/HEAD 概念の操作が意味を持つか）。</summary>
    public bool IsWorkingTree => Kind == GitCompareBaseKind.WorkingTree;

    /// <summary>ブランチ名を要する種別か（ブランチ選択 UI を出すかの判定）。</summary>
    public bool NeedsBranch => Kind is GitCompareBaseKind.Branch or GitCompareBaseKind.MergeBase;
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
