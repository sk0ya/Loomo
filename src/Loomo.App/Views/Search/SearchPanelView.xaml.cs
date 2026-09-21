using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.Services.Infrastructure;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

public partial class SearchPanelView : UserControl
{
    // AcceptSuggest が RootBox.Text を書き換えたときに TextChanged で候補を再表示しないためのガード。
    private bool _suppressRootSuggest;

    public SearchPanelView()
    {
        InitializeComponent();
        // パネルが開いた瞬間にクエリ欄へフォーカスして即入力できるようにする（VS Code 流）。
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private SearchPanelViewModel? Vm => DataContext as SearchPanelViewModel;

    /// <summary>検索ペインを既に表示済みの状態から舞台へ移した場合も、すぐ検索語を入力できるようにする。</summary>
    public void FocusQuery()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new System.Action(() =>
        {
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(QueryBox), QueryBox);
            QueryBox.Focus();
            Keyboard.Focus(QueryBox);
            QueryBox.SelectAll();
        }));
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            FocusQuery();
    }

    /// <summary>フォルダー節点と（一致行を持つ）ファイル見出しは、行のどこをクリックしても開閉する
    /// （小さな矢印を狙わせない＝フォルダーを畳めば配下のファイルを一気に閉じられる）。
    /// ファイル名検索のヒット（子を持たない）は選択＝プレビューに任せ、ここでは何もしない。</summary>
    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe)
            return;
        if (SearchResultTreePolicy.CanToggleExpansion(fe.DataContext)
            && WpfTreeTraversal.FindAncestor<TreeViewItem>(fe) is { } container)
            container.IsExpanded = !container.IsExpanded;
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
            SearchResultTreePolicy.SetExpanded(node, expanded);
    }

    // ===== 検索フォルダー欄（ワークスペースルート相対・フォルダパス補完） =====

    /// <summary>入力に応じてサブフォルダの補完候補を出す。候補は「入力済みのディレクトリ部分＋
    /// 入力中の名前」を前方一致で絞り、ワークスペースルート相対パスで提示する。</summary>
    private void OnRootBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressRootSuggest || !RootBox.IsKeyboardFocused)
            return;

        var matches = WorkspaceFolderSuggestions.Compute(Vm?.WorkspaceFolders, RootBox.Text);
        // 候補が無い／唯一の候補が入力そのものなら出さない（入力中の自動表示のみ抑制。Ctrl+Space の
        // 明示呼び出しは ShowRootSuggestions を直接使うのでここを通らない）。
        if (!WorkspaceFolderSuggestions.ShouldShow(matches, RootBox.Text))
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
        matches ??= WorkspaceFolderSuggestions.Compute(Vm?.WorkspaceFolders, RootBox.Text);
        if (matches.Count == 0)
        {
            CloseRootSuggest();
            return;
        }

        RootSuggestList.ItemsSource = matches;
        RootSuggestList.SelectedIndex = 0;
        RootSuggestPopup.IsOpen = true;
    }

    /// <summary>検索フォルダー欄のキー操作。補完ポップアップが開いていれば上下で候補移動・
    /// Enter/Tab で確定・Esc で閉じる。閉じているときは Ctrl+Space で候補を呼び出せる（一般的な
    /// エディタのインテリセンス起動キーに合わせる）・Enter で入力を即確定する。</summary>
    private void OnRootBoxKeyDown(object sender, KeyEventArgs e)
    {
        var action = WorkspaceFolderSuggestions.ResolveKeyAction(
            e.Key, Keyboard.Modifiers, RootSuggestPopup.IsOpen, RootSuggestList.SelectedItem is string);
        switch (action)
        {
            case RootSuggestionKeyAction.MoveNext:
                MoveRootSuggest(+1);
                e.Handled = true;
                break;
            case RootSuggestionKeyAction.MovePrevious:
                MoveRootSuggest(-1);
                e.Handled = true;
                break;
            case RootSuggestionKeyAction.Dismiss:
                CloseRootSuggest();
                e.Handled = true;
                break;
            case RootSuggestionKeyAction.Accept:
                if (RootSuggestList.SelectedItem is string suggestion)
                    AcceptRootSuggest(suggestion);
                e.Handled = true;
                break;
            case RootSuggestionKeyAction.Show:
                ShowRootSuggestions();
                e.Handled = true;
                break;
            case RootSuggestionKeyAction.Commit:
                CommitRootBox();
                CloseRootSuggest();
                e.Handled = true;
                break;
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
        var idx = WorkspaceFolderSuggestions.MoveSelectionIndex(
            count, RootSuggestList.SelectedIndex, delta);
        if (idx < 0)
            return;
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
        => WpfTreeTraversal.HasAncestor(node, RootSuggestList);

    /// <summary>一致行（grep）やファイル名ヒットを選択したらエディタでプレビューする（単クリック・矢印キー移動）。
    /// grep のファイル見出し（一致行を子に持つグループ）は展開用なのでプレビューしない。
    /// プレビュー（ActivateEditorTab 経由）はエディタコントロールへ同期的にキーボードフォーカスを奪う
    /// （PaneSplitView.Activate → FocusFocused）ため、選択変更の直前に結果ツリーがフォーカスを
    /// 持っていた（＝矢印キーやクリックでこのツリーを操作中だった）場合は、プレビュー後にフォーカスを
    /// ツリーへ戻し、引き続き矢印キーで選択を送れるようにする。ダブルクリック／Enter による明示的な
    /// Activate はこの対象外＝そのままエディタへフォーカスが移る（編集に入る意図のため）。</summary>
    private void OnResultSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        var hadFocus = ResultTree.IsKeyboardFocusWithin;
        if (!SearchResultTreePolicy.PreviewSelection(e.NewValue,
                match => Vm?.Preview(match), group => Vm?.Preview(group)))
            return;
        if (hadFocus)
            RestoreResultTreeFocus();
    }

    /// <summary>結果ツリーの現在選択中の節点へキーボードフォーカスを戻す（矢印キー操作を継続できるように）。
    /// プレビュー先のファイルが未読込／外部変更ありだと、読込み直しやそれに続く git 差分更新が非同期に
    /// 走り、その完了時にエディタコントロールが再度フォーカスを奪い返すことがある（この時点ではまだ
    /// 起きていないので検出できない）。ディスパッチャがアイドルになったところでもう一度確認・再取得する。</summary>
    private void RestoreResultTreeFocus()
    {
        if (!ResultTree.IsKeyboardFocusWithin)
            FocusSelectedResultItem();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ResultTree.IsKeyboardFocusWithin)
                FocusSelectedResultItem();
        }), DispatcherPriority.ApplicationIdle);
    }

    private void FocusSelectedResultItem()
    {
        if (WpfTreeTraversal.FindItemContainer<TreeViewItem>(ResultTree, item => item.IsSelected) is { } container)
            container.Focus();
        else
            ResultTree.Focus();
    }

    /// <summary>ダブルクリックで通常タブへ昇格（プレビューでなく確定して開く）。</summary>
    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ActivateSelectedResult())
            e.Handled = true;
    }

    private void OnResultKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ActivateSelectedResult())
            e.Handled = true;
    }

    private bool ActivateSelectedResult()
        => SearchResultTreePolicy.ActivateSelection(ResultTree.SelectedItem,
            match => Vm?.Activate(match), group => Vm?.Activate(group));

    /// <summary>このファイル内の一致をまとめて置換する（ファイル見出しの右クリックメニュー「置換」）。</summary>
    private void OnReplaceInGroupClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SearchFileGroup group } && Vm is { } vm)
            SearchReplacePresenter.ReplaceInFile(Window.GetWindow(this), vm, group);
    }

    /// <summary>ファイル見出しの右クリックメニューを開くたび、「置換」項目の表示可否を決める
    /// （置換欄を出しているとき、かつ一致行を持つグループ＝ファイル名検索のヒットには出さない）。</summary>
    private void OnFileContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm || Vm is not { } vm)
            return;
        SearchReplacePresenter.PrepareFileMenu(cm, vm);
    }

    /// <summary>この1件だけを置換する（一致行の右クリックメニュー「置換」）。確認ダイアログは無し
    /// （1件だけの操作は取り消しの心理的コストが低いので、ファイル一括／すべて置換とは違い都度確認しない）。</summary>
    private void OnReplaceOneContextClick(object sender, RoutedEventArgs e)
    {
        SearchReplacePresenter.ReplaceFromContextMenu(sender, Vm);
    }

    /// <summary>一致行の右クリックメニューを開くたび、「置換」項目の表示可否を決める
    /// （置換欄を出しているときだけ＝インラインの「置換」ボタンと同じ条件）。</summary>
    private void OnMatchContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm || Vm is not { } vm)
            return;
        SearchReplacePresenter.PrepareMatchMenu(cm, vm);
    }

    /// <summary>選択中の1件（未選択・すでに置換済みなら先頭の未置換）を置換して、次の未置換の一致へ
    /// 選択を移す（「すべて置換」の左の「置換」ボタン。Ctrl+H 系の「置換」＝1件ずつ進める操作に相当）。
    /// 置換しても一覧からは消えない（<see cref="SearchPanelViewModel.ReplaceOne"/>）ので、次の対象は
    /// そのまま同じ一覧から素直に探せる。</summary>
    private void OnReplaceSelectedClick(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;

        var (target, next) = SearchResultTreePolicy.NextUnreplaced(
            vm.AllFileGroups(), ResultTree.SelectedItem as SearchMatchItem);
        if (target is null)
            return;

        if (!SearchReplacePresenter.ReplaceOne(vm, target))
            return;

        if (next is not null
            && WpfTreeTraversal.FindItemContainer<TreeViewItem>(ResultTree,
                item => ReferenceEquals(item.DataContext, next)) is { } container)
        {
            container.IsSelected = true;
            container.BringIntoView();
        }
    }

    /// <summary>現在の検索結果すべてに置換を適用する（「すべて置換」ボタン）。</summary>
    private void OnReplaceAllClick(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
            SearchReplacePresenter.ReplaceAll(Window.GetWindow(this), vm);
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
