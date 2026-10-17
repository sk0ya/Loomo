using sk0ya.Loomo.Services.Terminal;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: コマンドコンポーザ（設計書 §23.2）。ターミナルペインの下部にはめ込まれ、 ヘッダーのトグルで表示/非表示。長い PowerShell を Vim エディタで組み立てて Ctrl+Enter（または ▶ ボタン）で直上の可視ターミナルへ送る。 本文はワークスペーススナップショットに保存・復元する。</summary>
public partial class ShellWindow {
    private const double ComposerDefaultHeight = 140;
    private VimEditorControl? _composerEditor;
    private string _composerPendingText = string.Empty;
    private double _composerHeight = ComposerDefaultHeight;
    private bool IsComposerVisible => ComposerSection.Visibility == Visibility.Visible;
    private void OnToggleComposer(object sender, RoutedEventArgs e)
        => SetComposerVisible(!IsComposerVisible);
    private void SetComposerVisible(bool visible) {
        if (visible == IsComposerVisible)
            return;
        if (visible) {
            EnsureComposerEditor();
            ComposerSection.Visibility = Visibility.Visible;
            ComposerSplitter.Visibility = Visibility.Visible;
            ComposerSplitterRow.Height = new GridLength(6);
            ComposerRow.Height = new GridLength(Math.Max(_composerHeight, 60));
            _composerEditor?.Focus();
        } else {
            if (ComposerRow.Height.IsAbsolute)
                _composerHeight = ComposerRow.Height.Value;
            ComposerSection.Visibility = Visibility.Collapsed;
            ComposerSplitter.Visibility = Visibility.Collapsed;
            ComposerSplitterRow.Height = new GridLength(0);
            ComposerRow.Height = new GridLength(0);
        }
    }
    private void EnsureComposerEditor() {
        if (_composerEditor is not null)
            return;
        var editor = new VimEditorControl(new VimEditorControlOptions()) {
            VimEnabled = _settings.Vim.Enabled, MinimalChrome = true, };
        _appearance.ApplyEditorAppearance(editor);
        editor.LinkClicked += OnEditorLinkClicked;
        editor.SetText(_composerPendingText);
        _composerEditor = editor;
        ComposerEditorHost.Child = editor;
    }
    /// <summary>
    /// Diff の左右並びで本文を出すエディタ。構文の登録（C# の色分け）・右クリックの見出し・Vim の有無は
    /// エディタペインと揃えるが、<b>LSP は渡さない</b>——CodeLens やインレイヒントは表示行を足し引きするので、
    /// 左右の行の揃いが崩れる（コンポーザーと同じ LSP 無しのエディタ）。
    /// </summary>
    private VimEditorControl CreateDiffEditorControl() =>
        new(new VimEditorControlOptions {
            EngineServices = _editorEngineServices,
            ContextMenuLabels = Services.EditorMenuLabels.Japanese,
        }) { VimEnabled = _settings.Vim.Enabled };

    private void ApplyDiffEditorAppearance(VimEditorControl control) {
        control.VimEnabled = _settings.Vim.Enabled;   // 共有ステータスバーを出すかどうかもこれで決まる
        _appearance.ApplyEditorOptions(control);
        _appearance.ApplyEditorAppearance(control);
    }
    private void OnComposerPreviewKeyDown(object sender, KeyEventArgs e) {
        if (_keybindings.For("composer.run") is { Count: 1 } seq
            && Input.KeyChord.FromEvent(e) is { } chord && chord.Equals(seq.First)) {
            e.Handled = true;
            RunComposer();
        }
    }
    private void OnComposerRun(object sender, RoutedEventArgs e) => RunComposer();
    private void OnComposerPinToPegboard(object sender, RoutedEventArgs e) {
        var text = CaptureComposerText();
        if (!string.IsNullOrWhiteSpace(text))
            _vm.Pegboard.AddContent(text, type: "text");
    }
    private void InsertIntoComposer(string text) {
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
    private void RunComposer()
    {
        if (_composerEditor is not { } editor)
            return;
        // 前に出ているタブで Claude Code が動いているなら、受け手はシェルではなく Claude（§38.5）。PowerShell の
        // 1行コマンドに組み直さず、本文をそのまま頼みごととして送る。複数行は bracketed paste で包めば
        // 1つの頼みごとのまま届く（行ごとに Enter が効いてばらばらに送信されない。Claude Code 2.1.302 で確認）。
        if (_activeTerminalTab is { } claudeTab && _paneActivity.AgentTabFor(claudeTab.Id) == claudeTab.Id) {
            if (string.IsNullOrWhiteSpace(editor.Text))
                return;
            var prompt = editor.Text.TrimEnd().Replace("\r\n", "\r").Replace('\n', '\r');
            claudeTab.View.SendTerminalInput("\u001b[200~" + prompt + "\u001b[201~");
            // 貼り付けを Claude が取り込み終えてから送信する（同じ書き込みに Enter を混ぜると貼り付けの一部になる）。
            var submit = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            submit.Tick += (_, _) => {
                submit.Stop();
                claudeTab.View.SendTerminalInput("\r");
            };
            submit.Start();
            SaveActiveWorkspaceSnapshot();
            return;
        }
        string? command;
        try {
            command = ComposerCommandBuilder.Build(editor.Text, ComposerCommandBuilder.DefaultScriptDirectory());
        } catch (Exception ex) {
            ToastService.Error($"コンポーザの実行準備に失敗しました: {ex.Message}");
            return;
        }
        if (command is null)
            return;
        if (_activeTerminalTab?.View is { } terminalView)
            _ = terminalView.RunCommandAsync(command, CancellationToken.None);
        SaveActiveWorkspaceSnapshot();
    }
    private void RestoreComposer(WorkspaceSnapshot workspace) {
        _composerPendingText = workspace.ComposerText ?? string.Empty;
        _composerEditor?.SetText(_composerPendingText);
        SetComposerVisible(workspace.ComposerVisible);
        _composerHeight = workspace.ComposerHeight is { } height and >= 60
            ? height
            : ComposerDefaultHeight;
        if (IsComposerVisible)
            ComposerRow.Height = new GridLength(_composerHeight);
    }
    private string CaptureComposerText()
        => _composerEditor?.Text ?? _composerPendingText;
    private double CaptureComposerHeight()
        => IsComposerVisible && ComposerRow.Height.IsAbsolute ? ComposerRow.Height.Value : _composerHeight;
}
