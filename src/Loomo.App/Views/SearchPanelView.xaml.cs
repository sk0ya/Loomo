using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

public partial class SearchPanelView : UserControl
{
    // 補完候補の最大件数。
    private const int MaxRootSuggestions = 20;
    // AcceptSuggest が RootBox.Text を書き換えたときに TextChanged で候補を再表示しないためのガード。
    private bool _suppressRootSuggest;

    public SearchPanelView() => InitializeComponent();

    private SearchPanelViewModel? Vm => DataContext as SearchPanelViewModel;

    // ===== 検索フォルダー欄（ワークスペースルート相対・フォルダパス補完） =====

    /// <summary>入力に応じてサブフォルダの補完候補を出す。候補は「入力済みのディレクトリ部分＋
    /// 入力中の名前」を前方一致で絞り、ワークスペースルート相対パスで提示する。</summary>
    private void OnRootBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressRootSuggest || !RootBox.IsKeyboardFocused)
            return;

        var matches = ComputeRootSuggestions(RootBox.Text);
        var text = RootBox.Text.Replace('\\', '/').TrimEnd('/');
        // 候補が無い／唯一の候補が入力そのものなら出さない。
        if (matches.Count == 0 ||
            (matches.Count == 1 && string.Equals(matches[0], text, StringComparison.OrdinalIgnoreCase)))
        {
            CloseRootSuggest();
            return;
        }

        RootSuggestList.ItemsSource = matches;
        RootSuggestList.SelectedIndex = 0;
        RootSuggestPopup.IsOpen = true;
    }

    private System.Collections.Generic.List<string> ComputeRootSuggestions(string input)
    {
        var empty = new System.Collections.Generic.List<string>();
        if (Vm?.WorkspaceRoot is not { } root || string.IsNullOrEmpty(root))
            return empty;

        var text = (input ?? "").Replace('\\', '/');
        var lastSep = text.LastIndexOf('/');
        var dirPart = lastSep >= 0 ? text[..lastSep] : "";
        var prefix = lastSep >= 0 ? text[(lastSep + 1)..] : text;

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

    /// <summary>検索フォルダー欄のキー操作。補完ポップアップが開いていれば上下で候補移動・
    /// Enter/Tab で確定・Esc で閉じる。閉じているときは Enter で入力を即確定する。</summary>
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

    /// <summary>置換欄で Enter を押したら一括置換を実行する（クリックと同じ）。</summary>
    private void OnReplaceBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ReplaceBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            _ = RunReplaceAllAsync();
            e.Handled = true;
        }
    }

    private void OnReplaceAllClick(object sender, RoutedEventArgs e) => _ = RunReplaceAllAsync();

    /// <summary>確認のうえ、表示中の検索結果に出ている全ファイルへ一括置換する（破壊的）。</summary>
    private async System.Threading.Tasks.Task RunReplaceAllAsync()
    {
        if (Vm is not { } vm || vm.Scope != SearchScope.Text || string.IsNullOrEmpty(vm.Query))
            return;

        var fileCount = vm.Results.Count(g => !string.IsNullOrEmpty(g.FullPath));
        if (fileCount == 0)
        {
            vm.StatusMessage = "置換対象がありません";
            return;
        }

        var owner = Window.GetWindow(this);
        var message =
            $"「{vm.Query}」を「{vm.Replacement}」に置換します。\n" +
            $"対象: {fileCount} ファイル（検索結果に出ているもの全体）。\n\n" +
            "この操作は元に戻せません。実行しますか？";
        var answer = owner is null
            ? MessageBox.Show(message, "一括置換", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            : MessageBox.Show(owner, message, "一括置換", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK)
            return;

        var (files, replacements) = await vm.ReplaceAllAsync();
        vm.StatusMessage = replacements == 0
            ? "置換は行われませんでした"
            : $"{replacements} 件を {files} ファイルで置換しました";
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
