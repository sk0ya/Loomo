using System.Windows.Input;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>ブランチ絞り込み欄でキーに割り当てる操作。</summary>
public enum BranchFilterKeyAction
{
    None,
    Clear,
    MoveToList,
}

/// <summary>コミット一覧でキーに割り当てる操作。</summary>
public enum GitLogKeyAction
{
    None,
    MoveDown,
    MoveUp,
    MoveTop,
    MoveBottom,
    OpenDiff,
    FocusFilter,
    ClearFilter,
}

/// <summary>キー1打の結果。<paramref name="PendingG"/> は "gg" の1つ目を待っている状態。</summary>
public readonly record struct GitLogKeyResult(GitLogKeyAction Action, bool PendingG);

/// <summary>Git ペインのキーボード操作。イベント入口とテスト互換 API を残し、操作は controller に委譲する。</summary>
public partial class GitSessionView
{
    private GitSessionKeyboardController _keyboardController = null!;

    /// <summary>既存テスト・呼び出し元向けの純粋なキー判定 API。</summary>
    public static BranchFilterKeyAction ResolveBranchFilterKey(Key key, string text, bool hasRows)
        => GitSessionKeyboardMapper.ResolveBranchFilterKey(key, text, hasRows) switch
        {
            BranchFilterKeyCommand.Clear => BranchFilterKeyAction.Clear,
            BranchFilterKeyCommand.MoveToList => BranchFilterKeyAction.MoveToList,
            _ => BranchFilterKeyAction.None,
        };

    private void OnBranchFilterKeyDown(object sender, KeyEventArgs e)
        => _keyboardController.OnBranchFilterKeyDown(sender, e);

    private async void OnBranchListKeyDown(object sender, KeyEventArgs e)
        => await _keyboardController.OnBranchListKeyDownAsync(sender, e);

    /// <summary>既存テスト・呼び出し元向けの純粋なキー判定 API。</summary>
    public static GitLogKeyResult ResolveLogKey(Key key, ModifierKeys modifiers, bool pendingG, bool hasFilters)
    {
        var mapped = GitSessionKeyboardMapper.ResolveLogKey(key, modifiers, pendingG, hasFilters);
        var action = mapped.Action switch
        {
            GitLogKeyCommand.MoveDown => GitLogKeyAction.MoveDown,
            GitLogKeyCommand.MoveUp => GitLogKeyAction.MoveUp,
            GitLogKeyCommand.MoveTop => GitLogKeyAction.MoveTop,
            GitLogKeyCommand.MoveBottom => GitLogKeyAction.MoveBottom,
            GitLogKeyCommand.OpenDiff => GitLogKeyAction.OpenDiff,
            GitLogKeyCommand.FocusFilter => GitLogKeyAction.FocusFilter,
            GitLogKeyCommand.ClearFilter => GitLogKeyAction.ClearFilter,
            _ => GitLogKeyAction.None,
        };
        return new(action, mapped.PendingG);
    }

    public void FocusCommitList() => _keyboardController.FocusCommitList();

    private void OnLogListLostFocus(object sender, KeyboardFocusChangedEventArgs e)
        => _keyboardController.OnLogListLostFocus(sender, e);

    private void OnLogListKeyDown(object sender, KeyEventArgs e)
        => _keyboardController.OnLogListKeyDown(sender, e);
}
