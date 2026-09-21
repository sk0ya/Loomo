using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Services.Infrastructure;

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
    /// コンテナの IsExpanded はノード VM と TwoWay で結ばれているので、VM にも伝わる。
    /// 2打目の <b>Down</b> は <see cref="OnTreePreviewMouseLeftButtonDown"/> が止める。</summary>
    private void OnTreeMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (CSharpSolutionExplorerPolicy.ExpansionTargetOnMouseUp(
                e.ClickCount, e.OriginalSource as DependencyObject) is { } item)
            item.IsExpanded = !item.IsExpanded;
    }

    /// <summary>ダブルクリックの2打目（WPF の TreeViewItem が内蔵する開閉）を、子を持つ行では止める。
    /// 1クリック開閉と重なると「Up で開いて Down で閉じる」で往復し、ちらついて元の状態に戻ってしまう
    /// ——止めた結果、ダブルクリックでも開閉はきっかり1回。開ける行（ファイル）は素通しで、
    /// そこは <see cref="OnTreeDoubleClick"/> が「開く」に使う。</summary>
    private void OnTreePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (CSharpSolutionExplorerPolicy.ShouldSuppressDoubleClickExpansion(
                e.ClickCount, e.OriginalSource as DependencyObject))
            e.Handled = true;
    }

    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not CSharpSolutionExplorerViewModel vm) return;
        if (e.OriginalSource is not DependencyObject source ||
            WpfTreeTraversal.FindAncestor<TreeViewItem>(source) is not { } item ||
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
        if (WpfTreeTraversal.FindDescendant<ScrollViewer>(SolutionTree) is not { } scrollViewer) return;

        // 対象はヘッダ行（Bd）のみ。item 全体だと展開済みの子を含む高さになる。
        var header = item.Template?.FindName("Bd", item) as FrameworkElement ?? item;
        if (!header.IsVisible) return;

        var top = header.TransformToVisual(scrollViewer).Transform(default).Y;
        var bottom = top + header.ActualHeight;
        var correction = CSharpSolutionExplorerPolicy.VerticalScrollCorrection(
            top, bottom, scrollViewer.ViewportHeight);
        if (correction != 0)
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + correction);
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

        var menu = CSharpSolutionExplorerContextMenuPresenter.Create(vm, node);

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

}
