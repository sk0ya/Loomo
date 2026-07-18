namespace sk0ya.Loomo.Services;

/// <summary>git reset のモード。</summary>
public enum GitResetMode
{
    Soft,
    Mixed,
    Hard
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
