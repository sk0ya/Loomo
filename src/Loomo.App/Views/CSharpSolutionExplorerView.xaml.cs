using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

public partial class CSharpSolutionExplorerView : UserControl
{
    public CSharpSolutionExplorerView() => InitializeComponent();

    private void OnBuildClick(object sender, RoutedEventArgs e)
        => RequestRootAction(CSharpSolutionAction.Build);

    private void OnTestClick(object sender, RoutedEventArgs e)
        => RequestRootAction(CSharpSolutionAction.Test);

    private void OnTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is CSharpSolutionExplorerViewModel vm &&
            e.NewValue is CSharpSolutionNodeViewModel node)
            vm.SelectedNode = node;
    }

    private void RequestRootAction(CSharpSolutionAction action)
    {
        if (DataContext is CSharpSolutionExplorerViewModel vm)
            vm.RequestTargetAction(action);
    }

    /// <summary>絞り込み欄のキー操作。Escで解除、↓でツリーへ抜ける（入力欄に囚われない）。</summary>
    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not CSharpSolutionExplorerViewModel vm) return;
        if (e.Key == Key.Escape)
        {
            vm.FilterText = "";
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Enter)
        {
            SolutionTree.Focus();
            if (SolutionTree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem first)
                first.Focus();
            e.Handled = true;
        }
    }

    /// <summary>1クリックで開閉する（フォルダーツリーと同じ操作）。開閉矢印は7px しかなく、
    /// 名前を狙って押しても何も起きないのは分かりにくいので、行のどこを押しても畳める。
    /// 矢印自身のクリックは ToggleButton 側で既にトグルされるため除外し、ダブルクリック
    /// （ClickCount=2）は二重トグルになるので無視する。開閉は TreeViewItem 側へ書く——
    /// コンテナの IsExpanded はノード VM と TwoWay で結ばれているので、VM にも伝わる。</summary>
    private void OnTreeMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || e.OriginalSource is not DependencyObject source) return;
        if (FindAncestor<ToggleButton>(source) is not null) return;
        if (FindAncestor<TreeViewItem>(source) is not { } item) return;
        if (item.DataContext is not CSharpSolutionNodeViewModel { Children.Count: > 0 }) return;

        item.IsExpanded = !item.IsExpanded;
    }

    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not CSharpSolutionExplorerViewModel vm) return;
        if (e.OriginalSource is not DependencyObject source ||
            FindAncestor<TreeViewItem>(source) is not { } item ||
            item.DataContext is not CSharpSolutionNodeViewModel node) return;

        // 開けない行（開閉するだけの行）は WPF 既定の扱いへ渡す。ここで握ると、
        // 1クリック開閉と合わせてダブルクリックの挙動がフォルダーツリーとズレる。
        if (!CSharpSolutionExplorerViewModel.CanOpen(node)) return;
        vm.Open(node);
        e.Handled = true;
    }

    /// <summary>TreeItemはUI Automation上でSelectionItemとして公開されるため、選択後のEnterでも
    /// ファイルを開けるようにする。マウスのダブルクリックと同じVM経路へ入り、キーボード／支援技術
    /// から別の操作を作らない。</summary>
    private void OnTreePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not CSharpSolutionExplorerViewModel vm) return;

        // Escでツリーから絞り込みを解除できると、深く潜ったあとの復帰が1打鍵で済む。
        if (e.Key == Key.Escape && vm.IsFiltering)
        {
            vm.FilterText = "";
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter ||
            sender is not TreeView tree ||
            tree.SelectedItem is not CSharpSolutionNodeViewModel node ||
            !CSharpSolutionExplorerViewModel.CanOpen(node))
            return;

        vm.Open(node);
        e.Handled = true;
    }

    /// <summary>選択・キーボード移動で WPF が出す BringIntoView を縦スクロールだけに絞る。
    /// 既定のままだと深い項目を見せようと横にもスクロールし、狭い左列では名前の頭が切れる
    /// （フォルダーツリーと同じ理由・同じ扱い）。</summary>
    private void OnItemRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (sender is not TreeViewItem item) return;
        e.Handled = true;

        // マウスで掴めた行は既に見えている。押下中は現在位置を保ち、キーボード移動だけ追従させる。
        if (Mouse.LeftButton == MouseButtonState.Pressed) return;
        if (FindDescendant<ScrollViewer>(SolutionTree) is not { } scrollViewer) return;

        // 対象はヘッダ行（Bd）のみ。item 全体だと展開済みの子を含む高さになる。
        var header = item.Template?.FindName("Bd", item) as FrameworkElement ?? item;
        if (!header.IsVisible) return;

        var top = header.TransformToVisual(scrollViewer).Transform(default).Y;
        var bottom = top + header.ActualHeight;
        if (top < 0)
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + top);
        else if (bottom > scrollViewer.ViewportHeight)
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + (bottom - scrollViewer.ViewportHeight));
    }

    /// <summary>クリック位置から最も近い祖先を探す（フォルダーツリーと同じ探索）。
    /// ItemsControl.ContainerFromElement はトップレベルのコンテナを返してしまうので使わない。</summary>
    private static T? FindAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        var current = source;
        while (current is not null and not T)
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        return current as T;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>行の右クリックメニュー。どの行でも何かしら出す——以前はソリューションと
    /// プロジェクト以外で何も出ず、右クリックが「壊れている」ように見えていた。</summary>
    private void OnTreeItemContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not TreeViewItem item ||
            DataContext is not CSharpSolutionExplorerViewModel vm ||
            item.DataContext is not CSharpSolutionNodeViewModel node)
        {
            e.Handled = true;
            return;
        }

        // 右クリックした行を選択へ入れる。ビルド対象（ActionTarget）もここから決まる。
        item.IsSelected = true;

        var menu = new ContextMenu();
        // 動的に生成するため、UI Automationからもソリューション操作の
        // メニューであることを安定して識別できるようにする。
        AutomationProperties.SetAutomationId(menu, "CSharpSolutionActions");
        AutomationProperties.SetName(menu, "C#ソリューション操作");

        if (node.Kind is CSharpSolutionNodeKind.Solution or CSharpSolutionNodeKind.Project)
            AddSolutionActions(menu, vm, node);
        else
            AddItemActions(menu, vm, node);

        if (menu.Items.Count == 0)
        {
            e.Handled = true;
            return;
        }

        item.ContextMenu = menu;
        // ContextMenu が未設定の状態で ContextMenuOpening に入った場合、ここで
        // 設定するだけでは今回の右クリックの表示判定に間に合わないWPF実装がある。
        // 今回のメニューを明示的に開いて、マウス操作とUI Automationの両方を同じ経路に通す。
        e.Handled = true;
        menu.PlacementTarget = item;
        menu.IsOpen = true;
    }

    private static void AddSolutionActions(
        ContextMenu menu, CSharpSolutionExplorerViewModel vm, CSharpSolutionNodeViewModel node)
    {
        AddAction(menu, vm, node, CSharpSolutionAction.Build, "ビルド");
        if (node.CanRunTests)
        {
            AddAction(menu, vm, node, CSharpSolutionAction.Test, "テスト");
            AddAction(menu, vm, node, CSharpSolutionAction.DebugTests, "テストをデバッグ");
        }
        menu.Items.Add(new Separator());
        if (node.Kind == CSharpSolutionNodeKind.Project)
            AddAction(menu, vm, node, CSharpSolutionAction.FixAllProject, "Fix All（プロジェクト）");
        else
            AddAction(menu, vm, node, CSharpSolutionAction.FixAllSolution, "Fix All（ソリューション）");
        if (node.Kind == CSharpSolutionNodeKind.Project)
        {
            menu.Items.Add(new Separator());
            AddAction(menu, vm, node, CSharpSolutionAction.Run, "実行");
            AddAction(menu, vm, node, CSharpSolutionAction.Debug, "デバッグ");
        }
        menu.Items.Add(new Separator());
        AddCommand(menu, "OpenProjectFile",
            node.Kind == CSharpSolutionNodeKind.Solution ? "ソリューションファイルを開く" : "プロジェクトファイルを開く",
            () => vm.OpenPath(node.FullPath));
        AddPathCommands(menu, node.FullPath);
    }

    /// <summary>ファイル・フォルダー・参照などの行。開く／パス／所属プロジェクトのビルド。</summary>
    private static void AddItemActions(
        ContextMenu menu, CSharpSolutionExplorerViewModel vm, CSharpSolutionNodeViewModel node)
    {
        if (CSharpSolutionExplorerViewModel.CanOpen(node))
            AddCommand(menu, "Open", "開く", () => vm.Open(node));
        AddPathCommands(menu, node.FullPath);

        // 所属プロジェクトを遡って提示する。ファイルを選んだままビルドしたい、が普通の流れ。
        var owner = node.Parent;
        while (owner is not null && owner.Kind != CSharpSolutionNodeKind.Project) owner = owner.Parent;
        if (owner is null) return;
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        AddAction(menu, vm, owner, CSharpSolutionAction.Build, $"{owner.Name} をビルド");
        if (owner.CanRunTests)
            AddAction(menu, vm, owner, CSharpSolutionAction.Test, $"{owner.Name} をテスト");
    }

    private static void AddPathCommands(ContextMenu menu, string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return;
        AddCommand(menu, "CopyPath", "パスをコピー", () =>
        {
            try { Clipboard.SetText(fullPath); } catch { /* クリップボード占有中は無視 */ }
        });
        AddCommand(menu, "RevealInExplorer", "エクスプローラーで表示", () =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"")
                {
                    UseShellExecute = true,
                });
            }
            catch { /* 失敗しても左列の操作は続行できる */ }
        });
    }

    private static void AddAction(
        ContextMenu menu,
        CSharpSolutionExplorerViewModel vm,
        CSharpSolutionNodeViewModel node,
        CSharpSolutionAction action,
        string header)
    {
        var item = new MenuItem { Header = header };
        AutomationProperties.SetAutomationId(item, $"CSharpSolutionAction.{action}");
        AutomationProperties.SetName(item, header);
        item.Click += (_, _) => vm.RequestAction(node, action);
        menu.Items.Add(item);
    }

    private static void AddCommand(ContextMenu menu, string id, string header, Action execute)
    {
        var item = new MenuItem { Header = header };
        AutomationProperties.SetAutomationId(item, $"CSharpSolutionCommand.{id}");
        AutomationProperties.SetName(item, header);
        item.Click += (_, _) => execute();
        menu.Items.Add(item);
    }
}
