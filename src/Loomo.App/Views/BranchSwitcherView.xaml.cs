using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ブランチ切替ポップアップの中身。タイトルバーと Git ペインヘッダーの両方が同じものを載せる
/// （以前は同じマークアップが2箇所に写経されていて、右クリックメニューだけを共用リソースに逃がすために
/// PlacementTarget から実体を引き当てる回りくどい作りになっていた）。
///
/// 構成は上から「同期帯（フェッチ／プル／プッシュ）」「絞り込み」「ブランチ一覧」「新規作成」。
/// 同期帯を一覧の外に固定しているのは操作の対象が違うから——フェッチはリモート、プル／プッシュは
/// 現在ブランチと上流に効くもので、一覧で選んだブランチには関係がない。一覧の行（と右クリック）は
/// 逆に、選んだ1本にだけ効く操作に限る。
///
/// DataContext は <see cref="GitSessionViewModel"/>（Git ペインと共有）。git 実行は VM に委ね、
/// 名前入力・破壊的操作の確認ダイアログはここが担う。成功時の表示更新は
/// GitService.RepositoryChanged → RefreshAsync の既存経路に乗る。
/// </summary>
public partial class BranchSwitcherView : UserControl
{
    public BranchSwitcherView()
    {
        InitializeComponent();
    }

    /// <summary>ポップアップを閉じてほしい（チェックアウト成功・ダイアログを出す直前など）。
    /// 実際に閉じるのは Popup を持つ側（ShellWindow）。</summary>
    public event EventHandler? CloseRequested;

    private GitSessionViewModel? Vm => DataContext as GitSessionViewModel;

    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 開く直前の初期化。前回の絞り込みが残っていると「ブランチが消えた」ように見えるので毎回消し、
    /// そのままキーボードで絞り込めるようフォーカスを入れる。
    /// </summary>
    public void PrepareForOpen()
    {
        StatusText.Visibility = Visibility.Collapsed;
        if (Vm is { } vm)
            vm.BranchFilter = "";
        // ポップアップが開いてレイアウトされた後でないとフォーカスが入らない
        Dispatcher.BeginInvoke(new Action(() => FilterBox.Focus()),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ShowError(string message)
    {
        StatusText.Text = message.Trim();
        StatusText.Visibility = Visibility.Visible;
    }

    /// <summary>Esc で絞り込みを消す（空ならポップアップごと閉じる）。</summary>
    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (Vm is { BranchFilter.Length: > 0 } vm)
            vm.BranchFilter = "";
        else
            Close();
        e.Handled = true;
    }

    // ===== 一覧 =====

    /// <summary>クリック行の TreeViewItem を辿る。展開矢印（ToggleButton, ClickMode=Press）上なら
    /// 既に開閉が処理済みなので null を返して二重に反応しない。</summary>
    private static TreeViewItem? FindRow(object? originalSource)
    {
        var element = originalSource as DependencyObject;
        while (element is not null and not TreeViewItem)
        {
            if (element is ToggleButton)
                return null;
            // OriginalSource が Run 等の FrameworkContentElement のことがある（VisualTreeHelper だと例外）
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return element as TreeViewItem;
    }

    private void OnTreeClick(object sender, MouseButtonEventArgs e)
        => _ = HandleTreeClickAsync(e);

    private async Task HandleTreeClickAsync(MouseButtonEventArgs e)
    {
        if (FindRow(e.OriginalSource) is not { DataContext: BranchTreeNode node } item)
            return;

        if (node.Branch is not { } branch)
        {
            // フォルダ・見出しは行のどこをクリックしても開閉する（メニューとしての手触り）
            item.IsExpanded = !item.IsExpanded;
            return;
        }

        if (branch.IsCurrent)
        {
            Close();
            return;
        }

        if (Vm is not { } vm) return;
        var result = await vm.CheckoutBranchAsync(branch);
        if (result is { Success: true })
        {
            Close();
        }
        else if (result is not null)
        {
            // 失敗（未コミット変更とのコンフリクト等）はポップアップを開いたまま理由を見せる
            ShowError(result.Message);
        }
        // result が null のときは他の git 操作が実行中（RunOpAsync が抑止）。何もしない
    }

    // ===== 行の右クリックメニュー =====

    private BranchTreeNode? SelectedNode => Tree.SelectedItem as BranchTreeNode;
    private GitBranchInfo? Target => SelectedNode?.Branch;

    private void OnTreeRightClickSelect(object sender, MouseButtonEventArgs e)
    {
        if (FindRow(e.OriginalSource) is { } item)
            item.IsSelected = true;
    }

    /// <summary>
    /// 対象が無い（フォルダ・見出しを右クリックした）ならメニューごと出さない。ブランチ行なら、
    /// そのブランチに意味を成さない項目を落とす（自分自身へのマージ／リベース、現在ブランチの削除、
    /// リモートブランチの削除＝git branch -d では消せない）。
    /// </summary>
    private void OnTreeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Target is not { } branch)
        {
            e.Handled = true;
            return;
        }

        MenuCheckout.IsEnabled = !branch.IsCurrent;
        MenuMerge.IsEnabled = !branch.IsCurrent;
        MenuRebase.IsEnabled = !branch.IsCurrent;
        MenuDelete.IsEnabled = !branch.IsCurrent && !branch.IsRemote;
    }

    private async void OnMenuCheckout(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Target is not { } branch) return;
        var result = await vm.CheckoutBranchAsync(branch);
        if (result is { Success: true })
            Close();
        else if (result is not null)
            ShowError(result.Message);
    }

    private async void OnMenuMerge(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Target is not { } branch) return;
        Close();
        await vm.MergeAsync(branch);
    }

    private async void OnMenuRebase(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Target is not { } branch) return;
        Close();
        var answer = MessageBox.Show(Window.GetWindow(this),
            $"現在のブランチを {branch.Name} の上へリベースします。コミットは作り直されます（履歴が書き換わります）。\n実行しますか？",
            "リベース", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
            await vm.RebaseAsync(branch);
    }

    private async void OnMenuCreateFrom(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Target is not { } branch) return;
        Close();
        var name = InputDialog.Prompt(Window.GetWindow(this), "新しいブランチ",
            $"{branch.Name} から作成するブランチ名を入力してください");
        if (!string.IsNullOrWhiteSpace(name))
            await vm.CreateBranchAsync(name, branch.Name);
    }

    private void OnMenuCopyName(object sender, RoutedEventArgs e)
    {
        if (Target is { } branch)
        {
            try { Clipboard.SetText(branch.Name); } catch { /* クリップボード占有中は無視 */ }
        }
        Close();
    }

    private async void OnMenuDelete(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Target is not { } branch) return;
        Close();
        var owner = Window.GetWindow(this);
        var answer = MessageBox.Show(owner, $"ブランチ {branch.Name} を削除しますか？",
            "ブランチ削除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        var result = await vm.DeleteBranchAsync(branch, force: false);
        if (result is { Success: false } &&
            result.Message.Contains("not fully merged", StringComparison.OrdinalIgnoreCase))
        {
            var forceAnswer = MessageBox.Show(owner,
                $"{branch.Name} はマージされていないコミットを含みます。強制削除（-D）しますか？\nコミットが失われる可能性があります。",
                "ブランチの強制削除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (forceAnswer == MessageBoxResult.Yes)
                await vm.DeleteBranchAsync(branch, force: true);
        }
    }

    // ===== 新規作成 =====

    private async void OnNewBranchClick(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        Close();
        var name = InputDialog.Prompt(Window.GetWindow(this), "新しいブランチ", "ブランチ名を入力してください");
        if (!string.IsNullOrWhiteSpace(name))
            await vm.CreateBranchAsync(name);
    }
}
