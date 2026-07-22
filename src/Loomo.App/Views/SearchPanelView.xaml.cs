using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

public partial class SearchPanelView : UserControl
{
    // 補完候補の最大件数。
    private const int MaxRootSuggestions = 20;
    // AcceptSuggest が RootBox.Text を書き換えたときに TextChanged で候補を再表示しないためのガード。
    private bool _suppressRootSuggest;

    public SearchPanelView()
    {
        InitializeComponent();
        // パネルが開いた瞬間にクエリ欄へフォーカスして即入力できるようにする（VS Code 流）。
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private SearchPanelViewModel? Vm => DataContext as SearchPanelViewModel;

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new System.Action(() =>
            {
                QueryBox.Focus();
                QueryBox.SelectAll();
            }));
    }

    /// <summary>フォルダー節点と（一致行を持つ）ファイル見出しは、行のどこをクリックしても開閉する
    /// （小さな矢印を狙わせない＝フォルダーを畳めば配下のファイルを一気に閉じられる）。
    /// ファイル名検索のヒット（子を持たない）は選択＝プレビューに任せ、ここでは何もしない。</summary>
    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe)
            return;
        var canToggle = fe.DataContext switch
        {
            SearchFolderNode => true,
            SearchFileGroup group => group.Count > 0,
            _ => false,
        };
        if (canToggle && FindContainer(fe) is { } container)
            container.IsExpanded = !container.IsExpanded;
    }

    private static TreeViewItem? FindContainer(DependencyObject node)
    {
        for (var cur = node; cur is not null; cur = System.Windows.Media.VisualTreeHelper.GetParent(cur))
            if (cur is TreeViewItem tvi)
                return tvi;
        return null;
    }

    // ===== すべて展開／すべて閉じる =====

    private void OnExpandAll(object sender, RoutedEventArgs e) => SetAllExpanded(true);
    private void OnCollapseAll(object sender, RoutedEventArgs e) => SetAllExpanded(false);

    /// <summary>結果ツリーの全フォルダー／ファイル節点の展開状態を一括で揃える
    /// （IsExpanded は TreeViewItem と双方向バインドなので、未生成のコンテナにも確実に効く）。</summary>
    private void SetAllExpanded(bool expanded)
    {
        if (Vm is null)
            return;
        foreach (var node in Vm.Results)
            SetExpandedRecursive(node, expanded);
    }

    private static void SetExpandedRecursive(object node, bool expanded)
    {
        switch (node)
        {
            case SearchFolderNode folder:
                folder.IsExpanded = expanded;
                foreach (var child in folder.Children)
                    SetExpandedRecursive(child, expanded);
                break;
            case SearchFileGroup group:
                group.IsExpanded = expanded;
                break;
        }
    }

    // ===== 検索フォルダー欄（ワークスペースルート相対・フォルダパス補完） =====

    /// <summary>入力に応じてサブフォルダの補完候補を出す。候補は「入力済みのディレクトリ部分＋
    /// 入力中の名前」を前方一致で絞り、ワークスペースルート相対パスで提示する。</summary>
    private void OnRootBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressRootSuggest || !RootBox.IsKeyboardFocused)
            return;

        var matches = ComputeRootSuggestions(RootBox.Text);
        var text = RootBox.Text.Replace('\\', '/').TrimEnd('/');
        // 候補が無い／唯一の候補が入力そのものなら出さない（入力中の自動表示のみ抑制。Ctrl+Space の
        // 明示呼び出しは ShowRootSuggestions を直接使うのでここを通らない）。
        if (matches.Count == 1 && string.Equals(matches[0], text, StringComparison.OrdinalIgnoreCase))
        {
            CloseRootSuggest();
            return;
        }

        ShowRootSuggestions(matches);
    }

    /// <summary>補完候補ポップアップを表示する。<paramref name="matches"/> 省略時は現在の入力から算出する。
    /// 一般的なエディタ流に Ctrl+Space からも明示的に呼べる（<see cref="OnRootBoxKeyDown"/>）。</summary>
    private void ShowRootSuggestions(System.Collections.Generic.List<string>? matches = null)
    {
        matches ??= ComputeRootSuggestions(RootBox.Text);
        if (matches.Count == 0)
        {
            CloseRootSuggest();
            return;
        }

        RootSuggestList.ItemsSource = matches;
        RootSuggestList.SelectedIndex = 0;
        RootSuggestPopup.IsOpen = true;
    }

    /// <summary>補完候補を組み立てる。単一ルートは従来通りワークスペースルート配下をブラウズする。
    /// マルチルートは <see cref="sk0ya.Loomo.App.ViewModels.SearchPanelViewModel.SearchRoot"/> と同じ
    /// 「フォルダー名/相対パス」表記に合わせ、まずフォルダー名（先頭セグメント）を候補にし、
    /// 一致するフォルダーが確定してからその配下をブラウズする。</summary>
    private System.Collections.Generic.List<string> ComputeRootSuggestions(string input)
    {
        var empty = new System.Collections.Generic.List<string>();
        var folders = Vm?.WorkspaceFolders;
        if (folders is null || folders.Count == 0)
            return empty;

        var text = (input ?? "").Replace('\\', '/');
        var lastSep = text.LastIndexOf('/');
        var dirPart = lastSep >= 0 ? text[..lastSep] : "";
        var prefix = lastSep >= 0 ? text[(lastSep + 1)..] : text;

        if (folders.Count == 1)
            return SuggestSubfolders(folders[0], dirPart, prefix);

        if (string.IsNullOrEmpty(dirPart))
        {
            // まだフォルダー名を入力中：ワークスペースフォルダーの表示名を候補にする。
            return folders
                .Select(LabelFor)
                .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Take(MaxRootSuggestions)
                .ToList();
        }

        // フォルダー名以降：先頭セグメントを解決し、そのフォルダー配下をブラウズする。
        var nameSep = dirPart.IndexOf('/');
        var rootName = nameSep >= 0 ? dirPart[..nameSep] : dirPart;
        var restDir = nameSep >= 0 ? dirPart[(nameSep + 1)..] : "";
        var folder = folders.FirstOrDefault(f => string.Equals(LabelFor(f), rootName, StringComparison.OrdinalIgnoreCase));
        if (folder is null)
            return empty;

        return SuggestSubfolders(folder, restDir, prefix)
            .Select(s => rootName + "/" + s)
            .ToList();
    }

    private static System.Collections.Generic.List<string> SuggestSubfolders(string root, string dirPart, string prefix)
    {
        var empty = new System.Collections.Generic.List<string>();
        string baseDir;
        try { baseDir = string.IsNullOrEmpty(dirPart) ? root : Path.GetFullPath(dirPart, root); }
        catch { return empty; }
        if (!Directory.Exists(baseDir))
            return empty;

        try
        {
            return Directory.EnumerateDirectories(baseDir)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n)
                            && !n!.StartsWith('.')
                            && n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Take(MaxRootSuggestions)
                .Select(n => string.IsNullOrEmpty(dirPart) ? n! : dirPart + "/" + n)
                .ToList();
        }
        catch
        {
            return empty;
        }
    }

    private static string LabelFor(string fullPath)
    {
        var name = Path.GetFileName(fullPath.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? fullPath : name;
    }

    /// <summary>検索フォルダー欄のキー操作。補完ポップアップが開いていれば上下で候補移動・
    /// Enter/Tab で確定・Esc で閉じる。閉じているときは Ctrl+Space で候補を呼び出せる（一般的な
    /// エディタのインテリセンス起動キーに合わせる）・Enter で入力を即確定する。</summary>
    private void OnRootBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (RootSuggestPopup.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Down: MoveRootSuggest(+1); e.Handled = true; return;
                case Key.Up: MoveRootSuggest(-1); e.Handled = true; return;
                case Key.Escape: CloseRootSuggest(); e.Handled = true; return;
                case Key.Enter:
                case Key.Tab:
                    if (RootSuggestList.SelectedItem is string s)
                    {
                        AcceptRootSuggest(s);
                        e.Handled = true;
                        return;
                    }
                    break;
            }
        }

        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ShowRootSuggestions();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            CommitRootBox();
            CloseRootSuggest();
            e.Handled = true;
        }
    }

    private void OnRootBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 候補（ポップアップ内）へフォーカスが移ったのでなければ閉じる。
        if (e.NewFocus is DependencyObject d && IsInsidePopup(d))
            return;
        CloseRootSuggest();
    }

    private void OnRootSuggestMouseDown(object sender, MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(RootSuggestList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item?.Content is string s)
        {
            AcceptRootSuggest(s);
            e.Handled = true;
        }
    }

    private void MoveRootSuggest(int delta)
    {
        var count = RootSuggestList.Items.Count;
        if (count == 0)
            return;
        var idx = Math.Clamp(RootSuggestList.SelectedIndex + delta, 0, count - 1);
        RootSuggestList.SelectedIndex = idx;
        RootSuggestList.ScrollIntoView(RootSuggestList.SelectedItem);
    }

    private void AcceptRootSuggest(string relativePath)
    {
        _suppressRootSuggest = true;
        RootBox.Text = relativePath;
        RootBox.CaretIndex = relativePath.Length;
        _suppressRootSuggest = false;

        CloseRootSuggest();
        RootBox.Focus();
        CommitRootBox();
    }

    private void CommitRootBox()
        => RootBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

    private void CloseRootSuggest() => RootSuggestPopup.IsOpen = false;

    private bool IsInsidePopup(DependencyObject node)
    {
        for (var cur = node; cur is not null; cur = System.Windows.Media.VisualTreeHelper.GetParent(cur))
            if (ReferenceEquals(cur, RootSuggestList))
                return true;
        return false;
    }

    /// <summary>一致行（grep）やファイル名ヒットを選択したらエディタでプレビューする（単クリック・矢印キー移動）。
    /// grep のファイル見出し（一致行を子に持つグループ）は展開用なのでプレビューしない。</summary>
    private void OnResultSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        switch (e.NewValue)
        {
            case SearchMatchItem match:
                Vm?.Preview(match);
                break;
            case SearchFileGroup group when group.Count == 0: // ファイル名ヒット
                Vm?.Preview(group);
                break;
        }
    }

    /// <summary>ダブルクリックで通常タブへ昇格（プレビューでなく確定して開く）。</summary>
    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultTree.SelectedItem is SearchMatchItem match)
        {
            Vm?.Activate(match);
            e.Handled = true;
        }
        else if (ResultTree.SelectedItem is SearchFileGroup group && group.Count == 0)
        {
            Vm?.Activate(group);
            e.Handled = true;
        }
    }

    private void OnResultKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        if (ResultTree.SelectedItem is SearchMatchItem match)
        {
            Vm?.Activate(match);
            e.Handled = true;
        }
        else if (ResultTree.SelectedItem is SearchFileGroup group && group.Count == 0)
        {
            Vm?.Activate(group);
            e.Handled = true;
        }
    }

    // ===== 置換 =====
    // ViewModel 側は実際の置換とファイルI/Oだけを持ち、確認ダイアログ・結果通知は他の一括操作
    // （FolderTree の削除等）と同じくここ（View 層）で行う。

    /// <summary>このファイル内の一致をまとめて置換する（ファイル見出しの「置換」ボタン）。</summary>
    private void OnReplaceInGroupClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SearchFileGroup group } || Vm is not { } vm)
            return;
        if (group.Count == 0)
            return;

        var confirm = MessageBox.Show(
            $"「{group.FileName}」内の {group.Count} 件を置換しますか？\nこの操作は元に戻せません。",
            "置換の確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
            return;

        vm.ReplaceInFile(group);
    }

    /// <summary>現在の検索結果すべてに置換を適用する（「すべて置換」ボタン）。</summary>
    private void OnReplaceAllClick(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;

        var groups = vm.AllFileGroups();
        var matchCount = groups.Sum(g => g.Count);
        if (matchCount == 0)
            return;

        var confirm = MessageBox.Show(
            $"{groups.Count} ファイルの {matchCount} 件を置換しますか？\nこの操作は元に戻せません。",
            "置換の確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
            return;

        var (files, matches) = vm.ReplaceAll();
        ToastService.Success($"{files} ファイルの {matches} 件を置換しました。");
    }

    /// <summary>クエリ欄のキー操作。Down で先頭ファイルへ移動、Esc でクエリをクリア（結果とエディタのハイライトも消える）。</summary>
    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && ResultTree.Items.Count > 0)
        {
            ResultTree.Focus();
            if (ResultTree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem first)
                first.IsSelected = true;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Vm is { } vm && !string.IsNullOrEmpty(vm.Query))
        {
            vm.ClearQuery();
            e.Handled = true;
        }
    }
}
