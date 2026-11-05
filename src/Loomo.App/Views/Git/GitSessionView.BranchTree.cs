using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// GitSessionView: 左列のツリー（ブランチ・ワークツリー・タグ・サブモジュール、§24.17.3）の右クリックメニューの
/// 出し分けと、ふだん畳んでおく絞り込み欄。
/// </summary>
public partial class GitSessionView
{
    /// <summary>「ワークツリー」の下位メニューの中身。ブランチの行ではその下位メニューに、ブランチを持たない
    /// ワークツリーの行ではメニューの最上段へ持ち上げて出す（1項目だけの下位メニューを開かせない）。</summary>
    private List<FrameworkElement> _treeMenuWorktreeItems = new();

    /// <summary>絞り込み欄を開いているか（文字を打った／"/"）。フォーカスが外れたら畳む。</summary>
    private bool _branchFilterOpen;

    private void CaptureTreeMenuWorktreeItems()
        => _treeMenuWorktreeItems = BranchMenuWorktree.Items.OfType<FrameworkElement>().ToList();

    /// <summary>
    /// ツリーの右クリック（と単クリックのメニュー）を行の種類に合わせる。項目の <c>Tag</c> が種類の印で、
    /// 印の無い項目はブランチ用。ブランチの可否（チェックアウトできるか等）はこの後で
    /// <see cref="GitBranchTreeMenuController"/> が付ける。
    /// </summary>
    internal bool PrepareTreeMenu(BranchTreeNode node)
    {
        var kind = GitTreeMenuKind.Of(node);
        if (kind is null || BranchList.ContextMenu is not { } menu)
            return false;

        MoveWorktreeItems(menu, hoist: kind == GitTreeMenuKind.Worktree);
        foreach (var element in menu.Items.OfType<FrameworkElement>())
            element.Visibility = GitTreeMenuKind.IsVisible(element.Tag as string, kind, node.HasWorktree)
                ? Visibility.Visible : Visibility.Collapsed;
        // git は同じブランチを2か所に置けないので、既に置いているブランチには「別の場所に置く」を出さない。
        BranchMenuCreateWorktree.Visibility = kind == GitTreeMenuKind.Branch && !node.HasWorktree && node.Branch?.IsRemote != true
            ? Visibility.Visible : Visibility.Collapsed;

        if (node.Worktree is { } worktree)
        {
            BranchMenuWorktree.Header = $"ワークツリー（{worktree.FolderName}）";
            PrepareWorktreeMenu(worktree);
        }
        return true;
    }

    private void MoveWorktreeItems(ContextMenu menu, bool hoist)
    {
        if (_treeMenuWorktreeItems.Count == 0) return;
        var target = hoist ? menu.Items : BranchMenuWorktree.Items;
        if (target.Contains(_treeMenuWorktreeItems[0])) return;
        var source = hoist ? BranchMenuWorktree.Items : menu.Items;
        foreach (var item in _treeMenuWorktreeItems)
        {
            source.Remove(item);
            target.Add(item);
        }
    }

    // ===== 絞り込み欄（ふだん畳む）=====

    /// <summary>
    /// 絞り込み欄を開いて入力欄へ。<paramref name="typed"/> はツリーで打った文字で、そのまま欄の続きに入れる
    /// （VS Code のツリー検索と同じ。打った1文字目を捨てない）。"/" からは null＝既存の語を選択状態に。
    /// </summary>
    private void OpenBranchFilter(string? typed)
    {
        _branchFilterOpen = true;
        UpdateBranchFilterRow();
        BranchFilterRow.UpdateLayout();
        BranchFilterBox.Focus();
        if (typed is null)
        {
            BranchFilterBox.SelectAll();
            return;
        }
        BranchFilterBox.Text += typed;
        BranchFilterBox.CaretIndex = BranchFilterBox.Text.Length;
    }

    private void OnBranchListTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text is not { Length: > 0 } text || char.IsControl(text[0]) || char.IsWhiteSpace(text[0]) || text == "/")
            return;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
            return;
        e.Handled = true;
        OpenBranchFilter(text);
    }

    private void OnBranchFilterLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _branchFilterOpen = false;
        UpdateBranchFilterRow();
    }

    /// <summary>欄を出すのは、開いている間・フォーカスがある間・<b>絞り込みが効いている間</b>
    /// （畳んだ拍子に効いている語ごと見えなくなると、ブランチが消えたように見える）。</summary>
    private void UpdateBranchFilterRow()
        => BranchFilterRow.Visibility = _branchFilterOpen || BranchFilterBox.IsKeyboardFocusWithin
            || Vm?.HasPaneBranchFilter == true
            ? Visibility.Visible : Visibility.Collapsed;

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GitSessionViewModel.HasPaneBranchFilter))
            UpdateBranchFilterRow();
    }
}

/// <summary>ツリーの行の種類と、右クリックメニューの項目（<c>Tag</c> の印）を出すかの判定（純ロジック）。</summary>
internal static class GitTreeMenuKind
{
    public const string Branch = "branch";
    public const string Worktree = "worktree";
    public const string Tag = "tag";
    public const string Submodule = "submodule";

    /// <summary>ブランチの行に付く「ワークツリー」の下位メニュー（置いているときだけ）。</summary>
    public const string BranchWorktree = "branch-worktree";

    public static string? Of(BranchTreeNode node)
        => node.Branch is not null ? Branch
            : node.Tag is not null ? Tag
            : node.Submodule is not null ? Submodule
            : node.IsWorktreeItem ? Worktree
            : null;

    public static bool IsVisible(string? itemTag, string kind, bool branchHasWorktree) => itemTag switch
    {
        null => kind == Branch,
        BranchWorktree => kind == Branch && branchHasWorktree,
        _ => itemTag == kind,
    };
}
