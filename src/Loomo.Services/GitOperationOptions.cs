namespace sk0ya.Loomo.Services;

/// <summary>git reset のモード。</summary>
public enum GitResetMode
{
    Soft,
    Mixed,
    Hard
}

/// <summary>
/// git pull の取り込み方。<see cref="Merge"/> は素の <c>git pull</c>（設定 <c>pull.rebase</c> に従う）で、
/// 残り2つは明示的に方式を指定する（Rider の Update Project 相当）。
/// </summary>
public enum GitPullMode
{
    /// <summary>既定。リポジトリ／ユーザーの <c>pull.rebase</c> 設定に従う。</summary>
    Merge,

    /// <summary>取り込んだ上にローカルコミットを載せ替える（<c>--rebase</c>）。</summary>
    Rebase,

    /// <summary>早送りできるときだけ取り込む（<c>--ff-only</c>）。マージコミットを作らない。</summary>
    FastForwardOnly
}

/// <summary>git merge の戦略。</summary>
public enum GitMergeStrategy
{
    Default,
    FastForwardOnly,
    NoFastForward,
    Squash
}

/// <summary>インタラクティブリベースの todo アクション。</summary>
public enum RebaseAction
{
    Pick,
    Reword,
    Edit,
    Squash,
    Fixup,
    Drop
}

/// <summary>インタラクティブリベース計画の1コミット。</summary>
public sealed record RebasePlanEntry(string Hash, string ShortHash, string Subject, RebaseAction Action)
{
    public RebasePlanEntry WithAction(RebaseAction action) => this with { Action = action };
}
