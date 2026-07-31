using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// タイトルバーのワークスペース切替ポップアップの中身。以前は ComboBox 1つで「切り替える」しかできず、
/// 名前・パス・最後に使った時期が読めない／同名フォルダを見分けられない／整理もできない、という状態だった。
/// 作りは <see cref="BranchSwitcherView"/>（ブランチ切替）に合わせてある——
/// 上から「絞り込み＋一覧の表示切替」「一覧」「フッター（ここに無いものを足す）」。
///
/// ただしブランチ側の<em>同期帯</em>（フェッチ／プル／プッシュ）にあたるものは置かない。あれは
/// 「リポジトリ全体に効く、一覧で選んだ行とは無関係な操作」だから固定帯に値するのであって、
/// パスのコピー・エクスプローラ・名前の変更は<em>1件に効く軽い操作</em>——同じ格で並べると重みが嘘になる。
/// 1件ぶんの操作は現在のワークスペースぶんも含めて全部行の右クリックへ集約し（現在のワークスペースの行も
/// 一覧に居るので特別扱いが要らない）、上には一覧の見せ方を変えるものだけを置く。
///
/// DataContext は <see cref="WorkspaceListViewModel"/>。永続化を伴う変更（切替・ピン留め・名前・削除）は
/// VM に委ね、フォルダ選択・名前入力・削除確認のような UI はここ／ShellWindow が持つ。
/// </summary>
public partial class WorkspaceSwitcherView : UserControl
{
    public WorkspaceSwitcherView()
    {
        InitializeComponent();
    }

    /// <summary>ポップアップを閉じてほしい。実際に閉じるのは Popup を持つ側（ShellWindow）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>ワークスペースを一覧から削除したい。確認ダイアログと「最後の1つは消せない」通知は
    /// ShellWindow 側（既存の削除経路）に任せる。</summary>
    public event EventHandler<WorkspaceEntryViewModel>? RemoveRequested;

    private WorkspaceListViewModel? Vm => DataContext as WorkspaceListViewModel;

    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>開く直前の初期化。前回の絞り込みが残っていると「ワークスペースが消えた」ように見えるので
    /// 毎回消し、フォルダの実在と相対時刻を取り直してから、そのまま絞り込めるようフォーカスを入れる。</summary>
    public void PrepareForOpen()
    {
        StatusText.Visibility = Visibility.Collapsed;
        if (Vm is { } vm)
        {
            vm.Filter = "";
            vm.Refresh();
            vm.SelectedWorkspace = vm.ActiveEntry;
        }
        // ポップアップが開いてレイアウトされた後でないとフォーカスが入らない
        Dispatcher.BeginInvoke(new Action(() => FilterBox.Focus()),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ShowError(string message)
    {
        StatusText.Text = message.Trim();
        StatusText.Visibility = Visibility.Visible;
    }

    // ===== 絞り込み欄のキー操作 =====

    /// <summary>Esc で絞り込みを消す（空ならポップアップごと閉じる）。↓ で一覧へ、Enter で選択中を開く。</summary>
    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (Vm is { Filter.Length: > 0 } filtered)
                    filtered.Filter = "";
                else
                    Close();
                e.Handled = true;
                break;
            case Key.Down:
                FocusList();
                e.Handled = true;
                break;
            case Key.Enter:
                Activate(Vm?.SelectedWorkspace);
                e.Handled = true;
                break;
        }
    }

    private void FocusList()
    {
        if (List.Items.Count == 0)
            return;
        var index = Math.Max(0, List.SelectedIndex);
        List.SelectedIndex = index;
        List.UpdateLayout();
        (List.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem)?.Focus();
    }

    // ===== 一覧 =====

    /// <summary>クリックされた行。行内のボタン（ピン留め）上なら、そちらが処理済みなので
    /// null を返す。展開したフォルダー行（<c>Tag="folder"</c>）も、ワークスペースの切替対象ではないので同様。</summary>
    private static ListBoxItem? FindRow(object? originalSource)
    {
        var element = originalSource as DependencyObject;
        while (element is not null and not ListBoxItem)
        {
            if (element is Button or FrameworkElement { Tag: "folder" })
                return null;
            // OriginalSource が Run 等の FrameworkContentElement のことがある（VisualTreeHelper だと例外）
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return element as ListBoxItem;
    }

    /// <summary>行のクリック＝そのワークスペースへ切替。ブランチのチェックアウトと違って、
    /// 切替は失っても困らない（元のワークスペースへ戻すだけ）ので、右クリック経由にはしない。</summary>
    private void OnListClick(object sender, MouseButtonEventArgs e)
    {
        if (FindRow(e.OriginalSource) is not { DataContext: WorkspaceEntryViewModel entry })
            return;

        e.Handled = true;
        Activate(entry);
    }

    private void OnListRightClickSelect(object sender, MouseButtonEventArgs e)
    {
        if (FindRow(e.OriginalSource) is { } item)
            item.IsSelected = true;
    }

    /// <summary>Enter で切替、Esc で閉じる、先頭で ↑ を押したら絞り込み欄へ戻る。</summary>
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Activate(Vm?.SelectedWorkspace);
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Up when List.SelectedIndex <= 0:
                FilterBox.Focus();
                FilterBox.CaretIndex = FilterBox.Text.Length;
                e.Handled = true;
                break;
        }
    }

    private void Activate(WorkspaceEntryViewModel? entry)
    {
        if (Vm is not { } vm || entry is null)
            return;

        // 切替は同期的に走って重いので、先にポップアップを畳んでから開始する。
        Close();
        vm.ActivateWorkspaceCommand.Execute(entry);
    }

    private void OnRowPinClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WorkspaceEntryViewModel entry })
            Vm?.TogglePinCommand.Execute(entry);
    }

    // ===== 追加フォルダー行の右クリックメニュー =====

    private static WorkspaceFolderEntryViewModel? FolderTarget(object sender)
        => (sender as FrameworkElement)?.DataContext as WorkspaceFolderEntryViewModel;

    private void OnFolderMenuCopyPath(object sender, RoutedEventArgs e)
    {
        if (FolderTarget(sender) is { } folder)
            CopyPath(folder.Path);
    }

    private void OnFolderMenuReveal(object sender, RoutedEventArgs e)
    {
        if (FolderTarget(sender) is { } folder)
            Reveal(folder.Path);
    }

    private void OnFolderMenuRemove(object sender, RoutedEventArgs e)
    {
        if (FolderTarget(sender) is not { } folder)
            return;

        var owner = Window.GetWindow(this);
        Close();
        var answer = MessageBox.Show(owner,
            $"「{folder.Owner.Label}」からフォルダー {folder.Path} を取り除きますか？\n" +
            "フォルダ自体は削除されません。", "ワークスペースフォルダーの削除",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.OK)
            Vm?.RemoveFolder(folder);
    }

    // ===== 行の右クリックメニュー =====

    private static WorkspaceEntryViewModel? MenuTarget(object sender)
        => (sender as FrameworkElement)?.DataContext as WorkspaceEntryViewModel;

    private void OnMenuActivate(object sender, RoutedEventArgs e) => Activate(MenuTarget(sender));

    private void OnMenuTogglePin(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is { } entry)
            Vm?.TogglePinCommand.Execute(entry);
    }

    private void OnMenuRename(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is { } entry)
            Rename(entry);
    }

    private void OnMenuCopyPath(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is { } entry)
            CopyPath(entry.RootPath);
    }

    private void OnMenuReveal(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is { } entry)
            Reveal(entry.RootPath);
    }

    private void OnMenuRemove(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is not { } entry)
            return;
        Close();
        RemoveRequested?.Invoke(this, entry);
    }

    // ===== 一覧の表示切替とフッター =====

    /// <summary>フォルダー（パス）の表示切替。ポップアップは開いたまま（一覧を見比べるための操作）。</summary>
    private void OnToggleAllFoldersClick(object sender, RoutedEventArgs e)
        => Vm?.ToggleFoldersCommand.Execute(null);

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        Close();
        Vm?.OpenFolderCommand.Execute(null);
    }

    // ===== 共通の小物 =====

    /// <summary>表示名の変更。空にすると既定（フォルダ名）へ戻る＝リセットも同じ入口で行える。</summary>
    private void Rename(WorkspaceEntryViewModel entry)
    {
        if (Vm is not { } vm)
            return;

        // 透明ポップアップはモーダルダイアログの上に浮くので、出す前に畳む（ブランチ側と同じ作法）。
        var owner = Window.GetWindow(this);
        Close();
        var name = InputDialog.Prompt(owner, "ワークスペースの表示名",
            $"「{entry.Label}」の表示名を入力してください（空にするとフォルダ名 {entry.Name} に戻ります）",
            entry.HasCustomName ? entry.Label : "", allowEmpty: true);
        if (name is null)
            return;

        vm.Rename(entry, name);
    }

    private void CopyPath(string path)
    {
        try { Clipboard.SetText(path); }
        catch { /* クリップボードのロック等は無視 */ }
        Close();
    }

    private void Reveal(string path)
    {
        // 失敗の理由はこのポップアップ内に出すので、成功したときだけ閉じる。
        if (!Directory.Exists(path))
        {
            ShowError($"フォルダが見つかりません: {path}");
            return;
        }
        try
        {
            Process.Start("explorer.exe", $"\"{path}\"");
            Close();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
}
