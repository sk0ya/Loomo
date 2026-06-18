using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ブランチ切替コントロール。現在ブランチの表示クリックでツリーポップアップを開き、
/// リーフ選択でチェックアウト、フォルダクリックで開閉する。タイトルバーと Git ペインヘッダーで
/// 同じ操作を共有するため、開閉・選択処理はどちらのポップアップ／状態表示にも効く形に切り出す。
/// git 操作自体は GitSessionViewModel（Git ペインと共有）に任せ、成功時の表示更新は
/// GitService.RepositoryChanged → RefreshAsync の既存経路に乗る。
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
}
