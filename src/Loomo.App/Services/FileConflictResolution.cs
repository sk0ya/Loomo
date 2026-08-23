namespace sk0ya.Loomo.App.Services;

/// <summary>コピー／移動先に同名の項目があるときの選択肢。</summary>
public enum FileConflictAction
{
    Overwrite,
    Skip,
    Rename,
    Cancel,
}

/// <summary>競合しているコピー／移動の情報。UI はこの値だけを使って確認画面を表示する。</summary>
public sealed record FileConflictContext(
    string SourcePath,
    string DestinationPath,
    bool IsDirectory,
    bool IsMove);

/// <summary>競合ダイアログの結果。ApplyToAll は同じ操作の残りの競合へ適用する。</summary>
public sealed record FileConflictDecision(
    FileConflictAction Action,
    string? NewName = null,
    bool ApplyToAll = false);

/// <summary>1件の貼り付け結果。キャンセルとスキップは履歴へ記録しない。</summary>
public sealed record FilePasteResult(
    string? DestinationPath,
    bool Skipped = false,
    bool Cancelled = false)
{
    public static FilePasteResult Skip() => new(null, Skipped: true);
    public static FilePasteResult Cancel() => new(null, Cancelled: true);
}
