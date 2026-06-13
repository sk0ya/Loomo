using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: コマンドパレット（部屋全体の操作統一）。移動・ペイン表示・タブ・コンポーザ・
/// ペグボード・サイドバー・ワークスペース切替といった既存操作に名前を付け、
/// Ctrl+Shift+P（または Ctrl+W p）から検索して実行できるようにする。
/// 一覧は開くたびに現在状態（ステージ中か・WS一覧など）から組み直す。
/// 絞り込みロジックは <see cref="PaletteFilter"/>（純ロジック・テスト済み）。
/// </summary>
public partial class ShellWindow
{
    private IReadOnlyList<PaletteCommand> _paletteCommands = Array.Empty<PaletteCommand>();

    private bool IsPaletteOpen => CommandPaletteOverlay.Visibility == Visibility.Visible;

    private void OpenCommandPalette()
    {
        _paletteCommands = BuildPaletteCommands();
        CommandPaletteOverlay.Visibility = Visibility.Visible;
        PaletteInput.Text = string.Empty;
        RefilterPalette();
        PaletteInput.Focus();
    }

    /// <param name="refocus">true なら直前にフォーカスしていたペインへ戻す（Esc・背景クリック時）。
    /// コマンド実行時は実行先がフォーカスを決めるので false。</param>
    private void CloseCommandPalette(bool refocus)
    {
        if (!IsPaletteOpen)
            return;
        CommandPaletteOverlay.Visibility = Visibility.Collapsed;
        if (refocus && _focusedRegion?.Pane is { } pane)
            FocusPane(pane);
    }

    private void RefilterPalette()
    {
        PaletteList.ItemsSource = PaletteFilter.Filter(_paletteCommands, PaletteInput.Text);
        if (PaletteList.Items.Count > 0)
        {
            PaletteList.SelectedIndex = 0;
            PaletteList.ScrollIntoView(PaletteList.SelectedItem);
        }
    }

    private void ExecutePaletteSelection()
    {
        if (PaletteList.SelectedItem is not PaletteCommand command)
            return;
        CloseCommandPalette(refocus: false);
        command.Execute();
    }

    private void OnPaletteTextChanged(object sender, TextChangedEventArgs e) => RefilterPalette();

    private void OnPaletteInputKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CloseCommandPalette(refocus: true);
                e.Handled = true;
                break;
            case Key.Enter:
                ExecutePaletteSelection();
                e.Handled = true;
                break;
            case Key.Down or Key.Up:
                MovePaletteSelection(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                break;
        }
    }

    private void MovePaletteSelection(int delta)
    {
        var count = PaletteList.Items.Count;
        if (count == 0)
            return;
        PaletteList.SelectedIndex = ((PaletteList.SelectedIndex < 0 ? 0 : PaletteList.SelectedIndex)
            + delta + count) % count;
        PaletteList.ScrollIntoView(PaletteList.SelectedItem);
    }

    /// <summary>背景（薄暗がり）クリックはキャンセル。</summary>
    private void OnPaletteBackgroundMouseDown(object sender, MouseButtonEventArgs e)
        => CloseCommandPalette(refocus: true);

    /// <summary>パレット本体のクリックは背景まで抜けさせない。</summary>
    private void OnPaletteBoxMouseDown(object sender, MouseButtonEventArgs e)
        => e.Handled = true;

    private void OnPaletteItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: PaletteCommand command })
        {
            e.Handled = true;
            CloseCommandPalette(refocus: false);
            command.Execute();
        }
    }

    /// <summary>現在状態からコマンド一覧を組む（開くたびに呼ぶ）。</summary>
    private List<PaletteCommand> BuildPaletteCommands()
    {
        var list = new List<PaletteCommand>();

        // ステージ
        list.Add(new("ステージ",
            _stageActive ? "ステージモードを解除（タイル表示へ）" : "ステージモードへ（舞台＋袖）",
            () => { if (_stageActive) ExitStageMode(); else EnterStageMode(); }));
        if (_stageActive)
            list.Add(new("ステージ", _overviewActive ? "俯瞰を閉じる" : "俯瞰（全カードを一望）",
                ToggleOverview, "Ctrl+W z"));

        // 移動（ステージ中は FocusPane がそのまま舞台転換になる）
        foreach (var kind in StageOrder)
        {
            var target = kind;
            list.Add(new("移動", $"{PaneLabel(target)} へ",
                () => { SetPaneVisible(target, true); FocusPane(target); }));
        }

        // ペイン表示
        foreach (var kind in StageOrder)
        {
            var target = kind;
            list.Add(new("ペイン", $"{PaneLabel(target)} の表示を切替",
                () => SetPaneVisible(target, !IsPaneVisible(target))));
        }

        // タブ
        list.Add(new("タブ", "新しいターミナルタブ", () => OnTerminalNewTab(this, new RoutedEventArgs())));
        list.Add(new("タブ", "新しいエディタタブ", () => OnEditorNewTab(this, new RoutedEventArgs())));
        list.Add(new("タブ", "新しいブラウザタブ", () => OnBrowserNewTab(this, new RoutedEventArgs())));

        // コンポーザ（作業台）
        list.Add(new("コンポーザ", IsComposerVisible ? "コンポーザを閉じる" : "コンポーザを開く",
            () => SetComposerVisible(!IsComposerVisible)));
        list.Add(new("コンポーザ", "本文をターミナルで実行", RunComposer, "Ctrl+Enter"));
        list.Add(new("コンポーザ", "本文をペグボードへピン",
            () => OnComposerPinToPegboard(this, new RoutedEventArgs())));

        // ペグボード（道具掛け）
        list.Add(new("ペグボード", "クリップボードから追加",
            () => _vm.Pegboard.AddFromClipboardCommand.Execute(null)));
        list.Add(new("ペグボード", "エディタの選択をピン", PinEditorSelectionToPegboard));
        list.Add(new("ペグボード", "ブラウザのURLをピン", PinBrowserUrlToPegboard));

        // サイドバー
        list.Add(new("サイドバー", "エクスプローラ", () => _vm.ShowExplorerCommand.Execute(null)));
        list.Add(new("サイドバー", "タブ一覧", () => _vm.ShowTabsCommand.Execute(null)));
        list.Add(new("サイドバー", "AIセッション", () => _vm.ShowSessionsCommand.Execute(null)));
        list.Add(new("サイドバー", "Git", () => _vm.ShowGitCommand.Execute(null)));
        list.Add(new("サイドバー", "ペグボード", () => _vm.ShowPegboardCommand.Execute(null)));
        list.Add(new("サイドバー", "設定", () => _vm.ShowSettingsCommand.Execute(null)));
        list.Add(new("サイドバー", "外観（テーマ）", () => _vm.ShowAppearanceCommand.Execute(null)));

        // ワークスペース
        foreach (var workspace in _vm.Workspaces.Workspaces.Where(w => !w.IsActive))
        {
            var target = workspace;
            list.Add(new("ワークスペース", $"切替: {target.Name}",
                () => _vm.Workspaces.ActivateWorkspaceCommand.Execute(target)));
        }

        return list;
    }
}
