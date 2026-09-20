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
