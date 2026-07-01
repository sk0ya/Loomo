using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ブランチ切替コントロール。現在ブランチの表示クリックでツリーポップアップを開き、
/// リーフ選択でチェックアウト、フォルダクリックで開閉する。タイトルバーと Git ペインヘッダーで
/// 同じ操作を共有するため、開閉・選択処理はどちらのポップアップ／状態表示にも効く形に切り出す。
/// git 操作自体は GitSessionViewModel（Git ペインと共有）に任せ、成功時の表示更新は
/// GitService.RepositoryChanged → RefreshAsync の既存経路に乗る。
/// 右クリックメニュー（チェックアウト／マージ／リベース／削除／フェッチ・プル・プッシュ等）は
/// タイトルバー・Git ペインの両ポップアップで1つの ContextMenu リソース（BranchTreeContextMenu）を
/// 共用する。フェッチ／プル／プッシュは XAML から GitSession の Command へ直接バインドするが、
/// チェックアウト等はダイアログ表示（履歴書き換えの確認等）を伴うためここでハンドルする。
/// </summary>
public partial class ShellWindow
{
    // ===== タイトルバー =====

    private void OnTitleBarBranchClick(object sender, RoutedEventArgs e)
        => ToggleBranchPopup(BranchPopup, BranchPopupStatus);

    private void OnTitleBarBranchTreeClick(object sender, MouseButtonEventArgs e)
        => _ = HandleBranchTreeClickAsync(e, BranchPopup, BranchPopupStatus);

    private async void OnTitleBarNewBranchClick(object sender, RoutedEventArgs e)
        => await CreateBranchFromPopupAsync(BranchPopup);

    // ===== Git ペインヘッダー =====

    private void OnGitPaneBranchClick(object sender, RoutedEventArgs e)
        => ToggleBranchPopup(GitPaneBranchPopup, GitPaneBranchPopupStatus);

    private void OnGitPaneBranchTreeClick(object sender, MouseButtonEventArgs e)
        => _ = HandleBranchTreeClickAsync(e, GitPaneBranchPopup, GitPaneBranchPopupStatus);

    private async void OnGitPaneNewBranchClick(object sender, RoutedEventArgs e)
        => await CreateBranchFromPopupAsync(GitPaneBranchPopup);

    // ===== 共有処理 =====

    private void ToggleBranchPopup(Popup popup, TextBlock status)
    {
        // 通常は起動時の遅延読込で済んでいるが、未読込でも開いた瞬間に一覧が出るよう保険をかける
        _vm.GitSession.EnsureLoaded();
        status.Visibility = Visibility.Collapsed;
        popup.IsOpen = !popup.IsOpen;
    }

    private async Task HandleBranchTreeClickAsync(MouseButtonEventArgs e, Popup popup, TextBlock status)
    {
        // クリック行の TreeViewItem を特定する。途中に展開矢印（ToggleButton, ClickMode=Press）が
        // あればそちらが既に開閉を処理しているので二重に反応しない。
        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not TreeViewItem)
        {
            if (element is ToggleButton)
                return;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        if (element is not TreeViewItem { DataContext: BranchTreeNode node } item)
            return;

        if (node.Branch is not { } branch)
        {
            // フォルダは行のどこをクリックしても開閉する（メニューとしての手触り）
            item.IsExpanded = !item.IsExpanded;
            return;
        }

        if (branch.IsCurrent)
        {
            popup.IsOpen = false;
            return;
        }

        var result = await _vm.GitSession.CheckoutBranchAsync(branch);
        if (result is { Success: true })
        {
            popup.IsOpen = false;
        }
        else if (result is not null)
        {
            // 失敗（未コミット変更とのコンフリクト等）はポップアップを開いたまま理由を見せる
            status.Text = result.Message.Trim();
            status.Visibility = Visibility.Visible;
        }
        // result が null のときは他の git 操作が実行中（RunOpAsync が抑止）。何もしない
    }

    private async Task CreateBranchFromPopupAsync(Popup popup)
    {
        popup.IsOpen = false;
        var name = InputDialog.Prompt(this, "新しいブランチ", "ブランチ名を入力してください");
        if (!string.IsNullOrWhiteSpace(name))
            await _vm.GitSession.CreateBranchAsync(name);
    }

    // ===== ブランチツリーの右クリックメニュー（タイトルバー／Git ペインの両ポップアップで共用） =====

    /// <summary>直近で右クリックされたツリー。ContextMenu の各 Click ハンドラはここから対象ブランチと
    /// 開閉すべきポップアップを引く（PlacementTarget 経由だと共用インスタンスで一意にならないため）。</summary>
    private TreeView? _branchContextTree;

    private void OnBranchTreeRightClickSelect(object sender, MouseButtonEventArgs e)
    {
        _branchContextTree = sender as TreeView;

        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not TreeViewItem)
        {
            if (element is ToggleButton)
                return;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        if (element is TreeViewItem item)
            item.IsSelected = true;
    }

    private GitBranchInfo? BranchContextTarget =>
        (_branchContextTree?.SelectedItem as BranchTreeNode)?.Branch;

    private Popup? BranchContextPopup => _branchContextTree?.Name switch
    {
        nameof(BranchPopupTree) => BranchPopup,
        nameof(GitPaneBranchPopupTree) => GitPaneBranchPopup,
        _ => null,
    };

    private async void OnBranchMenuCheckout(object sender, RoutedEventArgs e)
    {
        if (BranchContextTarget is not { } branch) return;
        var result = await _vm.GitSession.CheckoutBranchAsync(branch);
        if (result is { Success: true })
            CloseBranchContextPopup();
    }

    private async void OnBranchMenuMerge(object sender, RoutedEventArgs e)
    {
        if (BranchContextTarget is not { } branch) return;
        CloseBranchContextPopup();
        await _vm.GitSession.MergeAsync(branch);
    }

    private async void OnBranchMenuRebase(object sender, RoutedEventArgs e)
    {
        if (BranchContextTarget is not { } branch) return;
        CloseBranchContextPopup();
        var answer = MessageBox.Show(this,
            $"現在のブランチを {branch.Name} の上へリベースします。コミットは作り直されます（履歴が書き換わります）。\n実行しますか？",
            "リベース", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
            await _vm.GitSession.RebaseAsync(branch);
    }

    private async void OnBranchMenuCreateFrom(object sender, RoutedEventArgs e)
    {
        if (BranchContextTarget is not { } branch) return;
        CloseBranchContextPopup();
        var name = InputDialog.Prompt(this, "新しいブランチ",
            $"{branch.Name} から作成するブランチ名を入力してください");
        if (!string.IsNullOrWhiteSpace(name))
            await _vm.GitSession.CreateBranchAsync(name, branch.Name);
    }

    private void OnBranchMenuCopyName(object sender, RoutedEventArgs e)
    {
        if (BranchContextTarget is { } branch)
        {
            try { Clipboard.SetText(branch.Name); } catch { /* クリップボード占有中は無視 */ }
        }
        CloseBranchContextPopup();
    }

    private async void OnBranchMenuDelete(object sender, RoutedEventArgs e)
    {
        if (BranchContextTarget is not { } branch) return;
        CloseBranchContextPopup();
        var answer = MessageBox.Show(this, $"ブランチ {branch.Name} を削除しますか？",
            "ブランチ削除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        var result = await _vm.GitSession.DeleteBranchAsync(branch, force: false);
        if (result is { Success: false } &&
            result.Message.Contains("not fully merged", StringComparison.OrdinalIgnoreCase))
        {
            var forceAnswer = MessageBox.Show(this,
                $"{branch.Name} はマージされていないコミットを含みます。強制削除（-D）しますか？\nコミットが失われる可能性があります。",
                "ブランチの強制削除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (forceAnswer == MessageBoxResult.Yes)
                await _vm.GitSession.DeleteBranchAsync(branch, force: true);
        }
    }

    private void CloseBranchContextPopup()
    {
        if (BranchContextPopup is { } popup)
            popup.IsOpen = false;
    }
}
