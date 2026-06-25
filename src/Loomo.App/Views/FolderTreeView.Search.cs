using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

public partial class FolderTreeView
{
    // ===== 検索（/ → 入力 → n/N） =====

    private void OpenSearch()
    {
        _selectionBeforeSearchPath = (FileTree.SelectedItem as FileNodeViewModel)?.FullPath;

        SearchBar.Visibility = Visibility.Visible;
        SearchBox.Text = "";   // TextChanged 経由でフィルタも空に戻る
        SearchStatus.Text = "";

        // 直前まで Collapsed だったため、レイアウト確定後でないとフォーカスが移らない。
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => SearchBox.Focus()));
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchQuery = SearchBox.Text;

        // ツリーのフィルタ（重い列挙＋git）は VM 側でデバウンス＋バックグラウンド実行する。
        // ここでは即時にクエリだけ渡す（ハイライトの更新もこの設定で走る）。
        // 先頭ヒットの選択・件数表示は完了通知（FilterCompleted）で行う。
        if (DataContext is FolderTreeViewModel vm)
            vm.SearchFilter = _searchQuery;

        if (string.IsNullOrEmpty(_searchQuery))
        {
            _matches.Clear();
            _matchIndex = -1;
            UpdateSearchStatus();
        }
        else
        {
            SearchStatus.Text = "検索中…";
        }
    }

    // VM のバックグラウンドフィルタが Nodes へ反映され終わったら、先頭ヒットを選び件数を出す。
    private void OnFilterCompleted(object? sender, EventArgs e)
    {
        // 検索バーが開いている間だけ自動選択する（Enter 確定後などに割り込まない）。
        if (SearchBar.Visibility != Visibility.Visible)
            return;

        RebuildMatches();
        _matchIndex = _matches.Count > 0 ? 0 : -1;
        if (_matchIndex == 0)
        {
            // 入力中はフォーカスを検索ボックスに保ったまま、選択だけ先頭ヒットへ移す。
            // ツリーは直前に作り直されコンテナ未生成なので、レイアウト確定後にスクロールする。
            var first = _matches[0];
            first.IsSelected = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => SelectAndReveal(first, focus: false)));
        }

        UpdateSearchStatus();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                CloseSearch(commit: true);
                e.Handled = true;
                break;

            case Key.Escape:
                CloseSearch(commit: false);
                e.Handled = true;
                break;

            case Key.Down:
                MoveMatch(1, focusTree: false);
                e.Handled = true;
                break;

            case Key.Up:
                MoveMatch(-1, focusTree: false);
                e.Handled = true;
                break;
        }
    }

    private void CloseSearch(bool commit)
    {
        SearchBar.Visibility = Visibility.Collapsed;

        if (commit)
        {
            // 確定：フィルタは維持したまま、選択中のヒットへフォーカスを移す。
            // フィルタを残すことで「一致したファイルだけ」を引き続き辿れる（Esc で解除）。
            if (_matchIndex >= 0 && _matchIndex < _matches.Count)
                SelectAndReveal(_matches[_matchIndex], focus: true);
            else
                FileTree.Focus();
        }
        else
        {
            // キャンセル：フィルタを解除して全ツリーへ戻し、元の選択位置を復元する。
            ClearFilter();
            if (_selectionBeforeSearchPath is not null)
                RevealPath(_selectionBeforeSearchPath);
            else
                FileTree.Focus();
        }
    }

    private void ClearFilter()
    {
        _searchQuery = "";
        _matches.Clear();
        _matchIndex = -1;
        if (DataContext is FolderTreeViewModel vm)
            vm.SearchFilter = "";
    }

    private void MoveMatch(int delta, bool focusTree)
    {
        // ツリー更新でヒット集合が作り直されている場合に備え、毎回クエリから再計算する。
        var current = _matchIndex >= 0 && _matchIndex < _matches.Count ? _matches[_matchIndex] : null;
        RebuildMatches();

        if (_matches.Count == 0)
        {
            _matchIndex = -1;
            UpdateSearchStatus();
            return;
        }

        // 直前の位置を引き継ぐ。見失った（更新で別インスタンスになった）場合は端から。
        var baseIndex = current is not null ? _matches.IndexOf(current) : -1;
        if (baseIndex < 0)
            baseIndex = delta > 0 ? -1 : 0;

        _matchIndex = ((baseIndex + delta) % _matches.Count + _matches.Count) % _matches.Count;
        SelectAndReveal(_matches[_matchIndex], focus: focusTree);
        UpdateSearchStatus();
    }

    private void RebuildMatches()
    {
        _matches.Clear();
        if (DataContext is not FolderTreeViewModel vm || string.IsNullOrEmpty(_searchQuery))
            return;

        foreach (var node in VisibleNodes(vm.Nodes))
            if (node.Name.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase))
                _matches.Add(node);
    }

    private void UpdateSearchStatus()
    {
        SearchStatus.Text = _matches.Count > 0
            ? $"{_matchIndex + 1}/{_matches.Count}"
            : string.IsNullOrEmpty(_searchQuery) ? "" : "一致なし";
    }

    private void GoToEdge(bool last)
    {
        if (DataContext is not FolderTreeViewModel vm)
            return;

        var all = VisibleNodes(vm.Nodes).ToList();
        if (all.Count == 0)
            return;

        SelectAndReveal(last ? all[^1] : all[0], focus: true);
    }
}

