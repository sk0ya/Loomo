using System;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using sk0ya.Loomo.App.Services;
using Editor.Controls;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: コマンドコンポーザ（設計書 §23.2）。ターミナルペインの下部にはめ込まれ、
/// ヘッダーのトグルで表示/非表示。長い PowerShell を Vim エディタで組み立てて
/// Ctrl+Enter（または ▶ ボタン）で直上の可視ターミナルへ送る。
/// 本文はワークスペーススナップショットに保存・復元する。
/// </summary>
public partial class ShellWindow
{
    private const double ComposerDefaultHeight = 140;

    private VimEditorControl? _composerEditor;

    /// <summary>エディタ未生成の間に復元された本文（生成時に流し込む）。</summary>
    private string _composerPendingText = string.Empty;

    /// <summary>閉じる前の高さ（再表示で復元）。</summary>
    private double _composerHeight = ComposerDefaultHeight;

    private bool IsComposerVisible => ComposerSection.Visibility == Visibility.Visible;

    /// <summary>ターミナルヘッダーのトグル／コンポーザ内の ✕。</summary>
    private void OnToggleComposer(object sender, RoutedEventArgs e)
        => SetComposerVisible(!IsComposerVisible);

    private void SetComposerVisible(bool visible)
    {
        if (visible == IsComposerVisible)
            return;

        if (visible)
        {
            EnsureComposerEditor();
            ComposerSection.Visibility = Visibility.Visible;
            ComposerSplitter.Visibility = Visibility.Visible;
            ComposerSplitterRow.Height = new GridLength(6);
            ComposerRow.Height = new GridLength(Math.Max(_composerHeight, 60));
            _composerEditor?.Focus();
        }
        else
        {
            if (ComposerRow.Height.IsAbsolute)
                _composerHeight = ComposerRow.Height.Value;
            ComposerSection.Visibility = Visibility.Collapsed;
            ComposerSplitter.Visibility = Visibility.Collapsed;
            ComposerSplitterRow.Height = new GridLength(0);
            ComposerRow.Height = new GridLength(0);
        }
    }

    /// <summary>初回表示でエディタを実体化する（起動コストをかけない遅延生成）。</summary>
    private void EnsureComposerEditor()
    {
        if (_composerEditor is not null)
            return;

        // タブのエディタ（CreateEditorTab）と違い、Git 差分・分割/タブ橋渡し・共有ステータスバーは
        // 使わない素のコントロール。外観と Vim 有効設定だけ揃える。
        var editor = new VimEditorControl(new VimEditorControlOptions())
        {
            VimEnabled = _settings.Vim.Enabled,
        };
        ApplyEditorAppearance(editor);
        // 本文中のURLクリック（Ctrl+Click / gx）も内蔵ブラウザで開く（タブのエディタと同じ扱い）。
        editor.LinkClicked += OnEditorLinkClicked;
        editor.SetText(_composerPendingText);
        _composerEditor = editor;
        ComposerEditorHost.Child = editor;
    }

    /// <summary>コンポーザ内のどこでも、<c>composer.run</c> の実効ジェスチャ（既定 Ctrl+Enter）で実行する。
    /// Vim 編集と衝突しないトンネリングで先取りする。ウィンドウ全体のディスパッチャはこのコマンドの
    /// アクションを持たない（＝消費しない）ため、コンポーザにフォーカスがある時だけここで拾える。
    /// 単一ジェスチャのみ対応（連鎖を割り当てた場合はコンポーザ内ショートカットとしては無効）。</summary>
    private void OnComposerPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_keybindings.For("composer.run") is { Count: 1 } seq
            && Input.KeyChord.FromEvent(e) is { } chord && chord.Equals(seq.First))
        {
            e.Handled = true;
            RunComposer();
        }
    }

    private void OnComposerRun(object sender, RoutedEventArgs e) => RunComposer();

    /// <summary>「📌 ペグボードへ」：組み立てた本文をペグボードへピンして再利用できるようにする。</summary>
    private void OnComposerPinToPegboard(object sender, RoutedEventArgs e)
    {
        var text = CaptureComposerText();
        if (!string.IsNullOrWhiteSpace(text))
            _vm.Pegboard.AddContent(text, type: "text");
    }

    /// <summary>ペグボード等からの「コンポーザへ挿入」。表示して本文の末尾に足す（素材の流れ）。</summary>
    private void InsertIntoComposer(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        SetComposerVisible(true);
        var editor = _composerEditor!;   // SetComposerVisible(true) が実体化する
        var current = editor.Text;
        editor.SetText(string.IsNullOrWhiteSpace(current)
            ? text
            : current.TrimEnd('\r', '\n') + Environment.NewLine + text);
        editor.Focus();
    }

    /// <summary>コンポーザ本文を直上の可視ターミナルで実行する（複数行は一時 .ps1 経由・§23.2）。
    /// フォーカスはコンポーザに残す（コマンドを推敲しながら繰り返し実行する使い方のため）。</summary>
    private void RunComposer()
    {
        if (_composerEditor is not { } editor)
            return;

        string? command;
        try
        {
            command = ComposerCommandBuilder.Build(editor.Text, ComposerCommandBuilder.DefaultScriptDirectory());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"コンポーザの実行準備に失敗しました: {ex.Message}",
                "コンポーザ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (command is null)
            return;

        if (_activeTerminalTab?.View is { } terminalView)
            _ = terminalView.RunCommandAsync(command, CancellationToken.None);

        // 実行した本文は次回起動でも使えるよう即保存する。
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>ワークスペース切替時の復元（本文・表示状態・高さ）。開いたまま離れたら開いたまま戻る。</summary>
    private void RestoreComposer(WorkspaceSnapshot workspace)
    {
        _composerPendingText = workspace.ComposerText ?? string.Empty;
        _composerEditor?.SetText(_composerPendingText);

        // 順序に注意：SetComposerVisible(false) は現在の高さを _composerHeight へ退避するため、
        // 復元値の反映は表示切替の後に行う（切替前だと旧ワークスペースの高さで上書きされる）。
        SetComposerVisible(workspace.ComposerVisible);
        _composerHeight = workspace.ComposerHeight is { } height and >= 60
            ? height
            : ComposerDefaultHeight;
        if (IsComposerVisible)
            ComposerRow.Height = new GridLength(_composerHeight);
    }

    /// <summary>スナップショット保存時の本文捕捉。エディタ未生成なら直近の復元値を保つ。</summary>
    private string CaptureComposerText()
        => _composerEditor?.Text ?? _composerPendingText;

    /// <summary>スナップショット保存時の高さ捕捉（表示中はスプリッタで変わり得る現在値を優先）。</summary>
    private double CaptureComposerHeight()
        => IsComposerVisible && ComposerRow.Height.IsAbsolute ? ComposerRow.Height.Value : _composerHeight;
}
