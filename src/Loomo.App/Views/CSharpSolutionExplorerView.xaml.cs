using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

public partial class CSharpSolutionExplorerView : UserControl
{
    public CSharpSolutionExplorerView() => InitializeComponent();

    /// <summary>ツリー本体を表示しているか。false のときは見出し行だけを残して畳む。
    /// 高さの配分はホスト（IDE ペインの実行タブ）が持つため、状態変化は
    /// <see cref="SectionExpandedChanged"/> で知らせる。</summary>
    public bool IsSectionExpanded { get; private set; } = true;

    /// <summary><see cref="IsSectionExpanded"/> が変わった。ホストが行の高さを畳む／戻すために使う。</summary>
    public event EventHandler? SectionExpandedChanged;

    /// <summary>ホストから初期状態を復元するときに使う。状態が実際に変わったときだけ
    /// <see cref="SectionExpandedChanged"/> を発火する（同じ値なら何もしない）。</summary>
    public void SetSectionExpanded(bool expanded)
    {
        if (IsSectionExpanded == expanded) return;
        IsSectionExpanded = expanded;
        SectionBody.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        SectionToggle.Content = expanded ? "▾" : "▸";
        SectionToggle.ToolTip = expanded ? "ソリューションツリーを折りたたむ" : "ソリューションツリーを展開";
        SectionExpandedChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSectionToggleClick(object sender, RoutedEventArgs e)
        => SetSectionExpanded(!IsSectionExpanded);

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
            vm.RequestAction(vm.SelectedNode ?? vm.Nodes.FirstOrDefault(), action);
    }

    private void OnTreeDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not CSharpSolutionExplorerViewModel vm) return;
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not TreeViewItem)
            source = VisualTreeHelper.GetParent(source);
        if (source is TreeViewItem item && item.DataContext is CSharpSolutionNodeViewModel node)
        {
            vm.Open(node);
            e.Handled = true;
        }
    }

    /// <summary>TreeItemはUI Automation上でSelectionItemとして公開されるため、選択後のEnterでも
    /// ファイルを開けるようにする。マウスのダブルクリックと同じVM経路へ入り、キーボード／支援技術
    /// から別の操作を作らない。</summary>
    private void OnTreePreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter ||
            sender is not TreeView tree ||
            DataContext is not CSharpSolutionExplorerViewModel vm ||
            tree.SelectedItem is not CSharpSolutionNodeViewModel node ||
            node.Kind != CSharpSolutionNodeKind.File)
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
        if (System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed) return;
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

    private void OnTreeItemContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not TreeViewItem item ||
            DataContext is not CSharpSolutionExplorerViewModel vm ||
            item.DataContext is not CSharpSolutionNodeViewModel node ||
            node.Kind is not (CSharpSolutionNodeKind.Solution or CSharpSolutionNodeKind.Project))
        {
            e.Handled = true;
            return;
        }

        var menu = new ContextMenu();
        // 動的に生成するため、UI Automationからもソリューション操作の
        // メニューであることを安定して識別できるようにする。
        AutomationProperties.SetAutomationId(menu, "CSharpSolutionActions");
        AutomationProperties.SetName(menu, "C#ソリューション操作");
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
        item.ContextMenu = menu;
        // ContextMenu が未設定の状態で ContextMenuOpening に入った場合、ここで
        // 設定するだけでは今回の右クリックの表示判定に間に合わないWPF実装がある。
        // 今回のメニューを明示的に開いて、マウス操作とUI Automationの両方を同じ経路に通す。
        e.Handled = true;
        menu.PlacementTarget = item;
        menu.IsOpen = true;
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
}
