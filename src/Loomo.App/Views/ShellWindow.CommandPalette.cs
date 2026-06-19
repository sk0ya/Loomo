using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Abstractions;

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

    /// <summary>@／# モードの非同期検索を、入力が変わるたびにキャンセル＆再発行するためのトークン源。</summary>
    private CancellationTokenSource? _paletteSearchCts;

    /// <summary>パレットの入力モード。先頭の記号で切り替える（VS Code 風）。</summary>
    private enum PaletteMode { Command, File, Grep }

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
        _paletteSearchCts?.Cancel();
        CommandPaletteOverlay.Visibility = Visibility.Collapsed;
        if (refocus && _focusedRegion?.Pane is { } pane)
            FocusPane(pane);
    }

    /// <summary>先頭記号でモードと素のクエリへ分解する。@＝ファイル名、#＝grep、無印＝コマンド。</summary>
    private static (PaletteMode Mode, string Query) ParsePaletteMode(string? text)
    {
        text ??= string.Empty;
        if (text.StartsWith('@')) return (PaletteMode.File, text[1..].Trim());
        if (text.StartsWith('#')) return (PaletteMode.Grep, text[1..].Trim());
        return (PaletteMode.Command, text);
    }

    /// <summary>そのモードの先頭記号（コマンドは無印）。</summary>
    private static string ModePrefix(PaletteMode mode) => mode switch
    {
        PaletteMode.File => "@",
        PaletteMode.Grep => "#",
        _ => string.Empty,
    };

    /// <summary>素のクエリは保ったままモードだけ差し替える（先頭記号を付け替えてキャレットを末尾へ）。
    /// マウスでのチップ選択・Ctrl+Shift+P 連打の両方から呼ばれる。</summary>
    private void SetPaletteMode(PaletteMode mode)
    {
        var (_, query) = ParsePaletteMode(PaletteInput.Text);
        PaletteInput.Text = ModePrefix(mode) + query;     // TextChanged が RefilterPalette を呼ぶ
        PaletteInput.CaretIndex = PaletteInput.Text.Length;
        PaletteInput.Focus();
    }

    /// <summary>コマンド → ファイル名 → grep → コマンド… と巡回する（Ctrl+Shift+P 連打）。</summary>
    private void CyclePaletteMode()
    {
        var (mode, _) = ParsePaletteMode(PaletteInput.Text);
        var next = mode switch
        {
            PaletteMode.Command => PaletteMode.File,
            PaletteMode.File => PaletteMode.Grep,
            _ => PaletteMode.Command,
        };
        SetPaletteMode(next);
    }

    private void OnPaletteModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<PaletteMode>(tag, out var mode))
            SetPaletteMode(mode);
    }

    /// <summary>現在モードのチップを強調する（選択中＝Accent 枠＋通常文字色、他は淡色）。</summary>
    private void UpdateModeChips(PaletteMode mode)
    {
        Highlight(PaletteModeCommand, mode == PaletteMode.Command);
        Highlight(PaletteModeFile, mode == PaletteMode.File);
        Highlight(PaletteModeGrep, mode == PaletteMode.Grep);

        static void Highlight(Button chip, bool active)
        {
            if (active)
            {
                chip.SetResourceReference(Control.BorderBrushProperty, "Accent");
                chip.SetResourceReference(Control.ForegroundProperty, "Fg");
            }
            else
            {
                chip.BorderBrush = System.Windows.Media.Brushes.Transparent;
                chip.SetResourceReference(Control.ForegroundProperty, "FgDim");
            }
        }
    }

    private void RefilterPalette()
    {
        var (mode, query) = ParsePaletteMode(PaletteInput.Text);
        UpdateModeChips(mode);

        // 箱の幅は固定（モード切替で左右にズレないように）。検索モードだけ右にプレビュー枠を開く。
        var search = mode != PaletteMode.Command;
        PalettePreviewColumn.Width = search ? new GridLength(340) : new GridLength(0);

        if (mode == PaletteMode.Command)
        {
            _paletteSearchCts?.Cancel();
            ShowPaletteItems(PaletteFilter.Filter(_paletteCommands, query));
            return;
        }

        _ = RefilterSearchAsync(mode, query);
    }

    /// <summary>@／# モードの検索。直前の検索をキャンセルし、軽くデバウンスしてから走らせる。</summary>
    private async Task RefilterSearchAsync(PaletteMode mode, string query)
    {
        _paletteSearchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _paletteSearchCts = cts;
        var ct = cts.Token;

        try
        {
            await Task.Delay(120, ct); // 連続入力をまとめる

            IReadOnlyList<PaletteCommand> items;
            if (mode == PaletteMode.File)
            {
                var hits = await _search.FindFilesAsync(query, 50, ct);
                items = hits.Select(FileEntry).ToList();
            }
            else // Grep（空クエリは検索しない）
            {
                if (string.IsNullOrEmpty(query))
                {
                    ShowPaletteItems(Array.Empty<PaletteCommand>());
                    return;
                }
                var hits = await _search.GrepAsync(query, new GrepOptions(MaxResults: 200), ct);
                items = hits.Select(h => GrepEntry(h, query)).ToList();
            }

            if (!ct.IsCancellationRequested)
                ShowPaletteItems(items);
        }
        catch (OperationCanceledException) { /* 新しい入力に置き換わった */ }
    }

    private void ShowPaletteItems(IReadOnlyList<PaletteCommand> items)
    {
        PaletteList.ItemsSource = items;
        if (PaletteList.Items.Count > 0)
        {
            PaletteList.SelectedIndex = 0;
            PaletteList.ScrollIntoView(PaletteList.SelectedItem);
        }
        else
        {
            UpdatePalettePreview(null);
        }
    }

    private PaletteCommand FileEntry(FileSearchHit hit)
        => new("ファイル", hit.RelativePath, () => _ = OpenAndNavigateAsync(hit.FullPath, 0))
        { PreviewPath = hit.FullPath };

    private PaletteCommand GrepEntry(ContentSearchHit hit, string query)
        => new($"{hit.RelativePath}:{hit.Line}", hit.LineText.Trim(),
            () => _ = OpenAndNavigateAsync(hit.FullPath, hit.Line))
        { PreviewPath = hit.FullPath, PreviewLine = hit.Line, PreviewHighlight = query };

    /// <summary>ファイルをエディタタブで開き、行が指定されていればそこへジャンプする。</summary>
    private async Task OpenAndNavigateAsync(string path, int line)
    {
        await OpenFileInNewEditorTabAsync(path);
        if (line > 0 && _activeEditorTab?.Control is { } control)
            // line は1始まり、NavigateTo は0始まりなので変換する。
            control.NavigateTo(line - 1, 0);
    }

    private void OnPaletteSelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdatePalettePreview(PaletteList.SelectedItem as PaletteCommand);

    /// <summary>選択中ヒットのファイルを、該当行を中心に数行スニペット表示する。一致行は ▶ で印を付け、
    /// grep モードでは検索語をテーマ色でハイライトする。</summary>
    private void UpdatePalettePreview(PaletteCommand? command)
    {
        PalettePreview.Inlines.Clear();
        if (command?.PreviewPath is not { } path || !File.Exists(path))
            return;

        try
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0)
            {
                PalettePreview.Inlines.Add(new Run("(空ファイル)"));
                return;
            }

            const int radius = 14;
            var center = command.PreviewLine > 0 ? command.PreviewLine : 1;
            var start = Math.Max(1, center - radius);
            var end = Math.Min(lines.Length, center + radius);

            for (var i = start; i <= end; i++)
            {
                var gutter = new Run((command.PreviewLine > 0 && i == command.PreviewLine ? "▶" : " ")
                    + i.ToString().PadLeft(4) + "  ");
                gutter.SetResourceReference(TextElement.ForegroundProperty, "FgDim");
                PalettePreview.Inlines.Add(gutter);
                AppendHighlighted(lines[i - 1], command.PreviewHighlight);
                if (i < end)
                    PalettePreview.Inlines.Add(new LineBreak());
            }
        }
        catch
        {
            PalettePreview.Inlines.Add(new Run("(プレビューを読み込めません)"));
        }
        PalettePreviewScroll.ScrollToTop();
    }

    /// <summary>1 行を、検索語の出現箇所だけテーマ色（Accent 背景）で強調しつつ追加する。</summary>
    private void AppendHighlighted(string text, string? highlight)
    {
        if (string.IsNullOrEmpty(highlight))
        {
            PalettePreview.Inlines.Add(new Run(text));
            return;
        }

        var idx = 0;
        while (true)
        {
            var found = text.IndexOf(highlight, idx, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                PalettePreview.Inlines.Add(new Run(text[idx..]));
                break;
            }
            if (found > idx)
                PalettePreview.Inlines.Add(new Run(text[idx..found]));

            var hit = new Run(text.Substring(found, highlight.Length));
            hit.SetResourceReference(TextElement.BackgroundProperty, "Accent");
            hit.SetResourceReference(TextElement.ForegroundProperty, "Bg");
            PalettePreview.Inlines.Add(hit);

            idx = found + highlight.Length;
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

        // カタログコマンドの実効ジェスチャ（再割り当てに追従）。
        string? Sc(string id) => _keybindings.For(id)?.Format();

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
        list.Add(new("タブ", "新しいターミナルタブ", () => OnTerminalNewTab(this, new RoutedEventArgs()),
            Sc("tab.newTerminal"), "tab.newTerminal"));
        list.Add(new("タブ", "新しいエディタタブ", () => OnEditorNewTab(this, new RoutedEventArgs()),
            Sc("tab.newEditor"), "tab.newEditor"));
        list.Add(new("タブ", "新しいブラウザタブ", () => OnBrowserNewTab(this, new RoutedEventArgs()),
            Sc("tab.newBrowser"), "tab.newBrowser"));

        // コンポーザ（作業台）
        list.Add(new("コンポーザ", IsComposerVisible ? "コンポーザを閉じる" : "コンポーザを開く",
            () => SetComposerVisible(!IsComposerVisible)));
        list.Add(new("コンポーザ", "本文をターミナルで実行", RunComposer, Sc("composer.run"), "composer.run"));
        list.Add(new("コンポーザ", "本文をペグボードへピン",
            () => OnComposerPinToPegboard(this, new RoutedEventArgs())));

        // ペグボード（道具掛け）
        list.Add(new("ペグボード", "クリップボードから追加",
            () => _vm.Pegboard.AddFromClipboardCommand.Execute(null)));
        list.Add(new("ペグボード", "エディタの選択をピン", PinEditorSelectionToPegboard));
        list.Add(new("ペグボード", "ブラウザのURLをピン", PinBrowserUrlToPegboard));

        // サイドバー
        list.Add(new("サイドバー", "エクスプローラ", () => _vm.ShowExplorerCommand.Execute(null),
            Sc("sidebar.explorer"), "sidebar.explorer"));
        list.Add(new("サイドバー", "検索（全文検索 / grep）", () => _vm.ShowSearchCommand.Execute(null)));
        list.Add(new("サイドバー", "タブ一覧", () => _vm.ShowTabsCommand.Execute(null),
            Sc("sidebar.tabs"), "sidebar.tabs"));
        list.Add(new("サイドバー", "AIセッション", () => _vm.ShowSessionsCommand.Execute(null),
            Sc("sidebar.sessions"), "sidebar.sessions"));
        list.Add(new("サイドバー", "Git", () => _vm.ShowGitCommand.Execute(null),
            Sc("sidebar.git"), "sidebar.git"));
        list.Add(new("サイドバー", "ペグボード", () => _vm.ShowPegboardCommand.Execute(null),
            Sc("sidebar.pegboard"), "sidebar.pegboard"));
        list.Add(new("サイドバー", "設定", () => _vm.ShowSettingsCommand.Execute(null),
            Sc("sidebar.settings"), "sidebar.settings"));
        list.Add(new("サイドバー", "外観（テーマ）", () => _vm.ShowAppearanceCommand.Execute(null),
            Sc("sidebar.appearance"), "sidebar.appearance"));
        list.Add(new("サイドバー", "キーボード設定", () => _vm.ShowKeyboardSettingsCommand.Execute(null)));

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
