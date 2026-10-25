using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

public partial class GitPanelView
{
    public GitPanelView()
    {
        InitializeComponent();
        new GitPanelInteractionController(
            StagedList, WorkingTree, () => DataContext as GitPanelViewModel);
        // コミットメッセージで、変えたファイル名・識別子を補完する（§24.95）。
        new CommitMessageCompletionController(CommitMessageBox, () =>
            DataContext is GitPanelViewModel vm ? vm.LoadCommitCompletionCandidatesAsync() : Task.FromResult<IReadOnlyList<string>>([]));
    }

    /// <summary>「⤒」は左クリックで当て方（クリップボード／ファイル）のメニューを開く（§24.38.1）。</summary>
    private void OnApplyPatchMenuClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { ContextMenu: { } menu } button) return;
        menu.DataContext = DataContext;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // 作業ツリーの行の右クリック「別のワークツリーへ ▸」（§24.81）：開くたびにいまのワークツリー一覧から作り直す。
    // 対象の行は右クリックで選ばれている（WorkingTreeRowMenu のほかの項目と同じ）。
    private int _carryProbe;

    private async void OnCarrySubmenuOpened(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)
            || sender is not System.Windows.Controls.MenuItem menu
            || DataContext is not GitPanelViewModel vm)
            return;
        var item = WorkingTree.SelectedItem is GitChangeTreeNode { Change: { } change } ? change : null;
        var probe = ++_carryProbe;
        menu.Items.Clear();
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "探しています…", IsEnabled = false });
        var targets = await vm.CarryTargetsAsync();
        if (probe != _carryProbe || !menu.IsSubmenuOpen)
            return;
        menu.Items.Clear();
        if (targets.Count == 0)
        {
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "ほかのワークツリーはありません", IsEnabled = false });
            return;
        }
        foreach (var target in targets)
        {
            var label = target.Branch is { Length: > 0 } branch
                ? $"{branch}（{System.IO.Path.GetFileName(target.Path.TrimEnd('\\', '/'))}）"
                : System.IO.Path.GetFileName(target.Path.TrimEnd('\\', '/'));
            var row = new System.Windows.Controls.MenuItem { Header = label, ToolTip = target.Path };
            row.Items.Add(Action("写す（こちらにも残す）", move: false));
            row.Items.Add(Action("移す（こちらからは消す）", move: true));
            menu.Items.Add(row);

            System.Windows.Controls.MenuItem Action(string header, bool move)
            {
                var action = new System.Windows.Controls.MenuItem { Header = header };
                action.Click += (_, _) => _ = vm.CarryChangesAsync(item, target, move);
                return action;
            }
        }
    }
}
