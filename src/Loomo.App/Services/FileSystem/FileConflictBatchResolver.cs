namespace sk0ya.Loomo.App.Services;

/// <summary>複数項目の貼り付けで「すべてに適用」を記憶する競合解決ラッパー。</summary>
public sealed class FileConflictBatchResolver
{
    private readonly Func<FileConflictContext, FileConflictDecision> _resolve;
    private FileConflictDecision? _applyToAll;

    public FileConflictBatchResolver(Func<FileConflictContext, FileConflictDecision> resolve)
        => _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));

    public FileConflictDecision Resolve(FileConflictContext context)
    {
        if (_applyToAll is { } remembered)
            return remembered;

        var decision = _resolve(context);
        // 名前変更は項目ごとに名前が必要なので、全件適用は上書き／スキップだけにする。
        if (decision.ApplyToAll && FileConflictDecisionPolicy.CanApplyToAll(decision.Action))
            _applyToAll = decision with { ApplyToAll = false };
        return decision;
    }
}
