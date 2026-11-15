using System.Windows.Threading;
using sk0ya.Loomo.Services.ClaudeCode;
using Terminal.Sessions;

namespace sk0ya.Loomo.App.Views;
/// <summary>
/// ShellWindow: Claude Code の IDE 連携（§38）。Loomo を VS Code 拡張と同じ口で Claude Code に見せる。
/// <list type="bullet">
/// <item>Loomo のターミナルには <c>CLAUDE_CODE_SSE_PORT</c> などを渡し、そこで起動した claude が自分から繋いでくる。</item>
/// <item>アクティブなエディタの選択（無ければキャレット）を <c>selection_changed</c> で送る——Claude は
///   「いま人間がどこを見ているか」を頼まれなくても知っている。</item>
/// <item><c>getDiagnostics</c> には問題一覧の全件（表示の絞り込みを通さない）で答える。</item>
/// <item>編集の提案（<c>openDiff</c>）は Diff で見せ、ターミナルペイン上端のバーで受け入れ／却下する。</item>
/// </list>
/// サーバーは別スレッドで動くので、ホストの答えはどれも Dispatcher を通す。
/// </summary>
public partial class ShellWindow {
    private ClaudeIdeServer? _claudeIde;
    private IReadOnlyList<string> _claudeIdeFolders = [];
    private DispatcherTimer? _claudeSelectionTimer;
    private VimEditorControl? _claudeSelectionSource;
    private ClaudeIdeSelection? _claudeLastSelection;

    private void InitializeClaudeIde() {
        _vm.ClaudeProposal.ShowDiffRequested += ShowClaudeProposalDiff;
        _vm.ClaudeProposal.Accepted += path => {
            // 書くのは Claude（受け入れの返事を受けてから）。少し待ってから開いているタブを読み直す。
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += async (_, _) => {
                timer.Stop();
                await ReloadEditorTabsFromDiskAsync([path]);
            };
            timer.Start();
        };
        if (!_settings.ClaudeCodeIdeIntegration)
            return;
        _claudeIdeFolders = _workspace.Folders.ToArray();
        _workspace.FoldersChanged += OnClaudeIdeFoldersChanged;
        try {
            _claudeIde = new ClaudeIdeServer(new ClaudeIdeHost(this), () => _claudeIdeFolders);
            _claudeIde.Start();
        } catch (Exception ex) {
            System.Diagnostics.Trace.WriteLine($"Claude Code の IDE 連携を開始できませんでした: {ex.Message}");
            _claudeIde?.Dispose();
            _claudeIde = null;
        }
    }
    private void OnClaudeIdeFoldersChanged(object? sender, EventArgs e) {
        _claudeIdeFolders = _workspace.Folders.ToArray();
        _claudeIde?.UpdateWorkspaceFolders();
    }
    private void DisposeClaudeIde() {
        _workspace.FoldersChanged -= OnClaudeIdeFoldersChanged;
        _claudeSelectionTimer?.Stop();
        _claudeIde?.Dispose();
        _claudeIde = null;
    }

    /// <summary>ターミナルのシェルへ渡す環境変数。タブの ID は Claude Code のフックが「どのタブの claude か」を
    /// Loomo へ名乗るのに使う（§24.1.1）。</summary>
    private Dictionary<string, string> TerminalEnvironmentFor(Guid tabId) {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["LOOMO_TERMINAL_TAB"] = tabId.ToString(),
        };
        if (_claudeIde is { } server)
            foreach (var (key, value) in server.TerminalEnvironment)
                environment[key] = value;
        if (_claudeHookPipeName is { } pipe)
            environment["LOOMO_CLAUDE_HOOK_PIPE"] = pipe;
        AddEditWaitEnvironment(environment);
        return environment;
    }
    private static TerminalSessionRequest WithEnvironment(TerminalSessionRequest request, IReadOnlyDictionary<string, string> extra) {
        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in request.EnvironmentVariables)
            merged[key] = value;
        foreach (var (key, value) in extra)
            merged[key] = value;
        return request with { EnvironmentVariables = merged };
    }

    // ── 選択の通知 ───────────────────────────────────────────────
    private void WireEditorForClaudeIde(VimEditorControl control) {
        control.SelectionChanged += (_, _) => ScheduleClaudeSelection(control);
        control.CaretMoved += (_, _) => ScheduleClaudeSelection(control);
    }
    /// <summary>選択はキー入力ごとに動くので、落ち着いてから（200ms）1回だけ送る。誰も繋いでいなければ何もしない
    /// ——エディタの打鍵の経路に余計な仕事を足さない（§31.15）。</summary>
    private void ScheduleClaudeSelection(VimEditorControl control) {
        if (_claudeIde is not { ConnectionCount: > 0 })
            return;
        _claudeSelectionSource = control;
        if (_claudeSelectionTimer is null) {
            _claudeSelectionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _claudeSelectionTimer.Tick += (_, _) => {
                _claudeSelectionTimer.Stop();
                PublishClaudeSelection();
            };
        }
        _claudeSelectionTimer.Stop();
        _claudeSelectionTimer.Start();
    }
    private void PublishClaudeSelection() {
        if (_claudeIde is not { } server || _claudeSelectionSource is not { } control
            || !ReferenceEquals(_activeEditorTab is { IsRealized: true } tab ? tab.Control : null, control)
            || SelectionOf(control) is not { } selection
            || selection == _claudeLastSelection)
            return;
        _claudeLastSelection = selection;
        server.PublishSelection(selection);
    }
    private static ClaudeIdeSelection? SelectionOf(VimEditorControl control) {
        if (control.FilePath is not { Length: > 0 } path)
            return null;
        if (control.HasSelection && control.SelectionAsLspRange() is { } range)
            return new ClaudeIdeSelection(path, control.SelectedText,
                range.Start.Line, range.Start.Character, range.End.Line, range.End.Character);
        var caret = control.Caret;
        return new ClaudeIdeSelection(path, "", caret.Line, caret.Column, caret.Line, caret.Column);
    }

    // ── 編集の提案 ───────────────────────────────────────────────
    private void ShowClaudeProposalDiff(string path, string contents) {
        var name = Path.GetFileName(path);
        var current = FindEditorTabByPath(path) is { IsRealized: true } tab
            ? tab.Control.Text
            : File.Exists(path) ? ReadTextOrEmpty(path) : "";
        ShowDiff(new DiffOpenTarget.Comparison(new DiffComparison(
            $"いま: {name}", current, $"Claude の提案: {name}", contents, path)));
    }
    private static string ReadTextOrEmpty(string path) {
        try { return File.ReadAllText(path); }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }
    private EditorTab? FindEditorTabByPath(string path)
        => _editorTabs.FirstOrDefault(tab => tab.PeekFilePath is { Length: > 0 } open && FilePathRelations.AreEqual(open, path));

    /// <summary>Claude Code からの問い合わせに答える。呼ばれるのはサーバーのスレッドなので、すべて UI へ渡す。</summary>
    private sealed class ClaudeIdeHost(ShellWindow window) : IClaudeIdeHost {
        private Task<T> OnUi<T>(Func<T> read) => window.Dispatcher.InvokeAsync(read).Task;
        private Task<T> OnUi<T>(Func<Task<T>> run) => window.Dispatcher.InvokeAsync(run).Task.Unwrap();

        public Task<ClaudeIdeSelection?> GetCurrentSelectionAsync() => OnUi(() =>
            window._activeEditorTab is { IsRealized: true } tab ? SelectionOf(tab.Control) : null);

        public Task<IReadOnlyList<ClaudeIdeEditor>> GetOpenEditorsAsync() => OnUi<IReadOnlyList<ClaudeIdeEditor>>(() =>
            window._editorTabs
                .Where(tab => tab.PeekFilePath is { Length: > 0 })
                .Select(tab => new ClaudeIdeEditor(tab.PeekFilePath!, ReferenceEquals(tab, window._activeEditorTab),
                    tab.IsRealized && tab.Control.IsModified))
                .ToList());

        public Task<IReadOnlyList<ClaudeIdeFileDiagnostics>> GetDiagnosticsAsync(string? filePath)
            => OnUi<IReadOnlyList<ClaudeIdeFileDiagnostics>>(() =>
                window._vm.Debug.Problems.AllProblems()
                    .Where(item => filePath is null || FilePathRelations.AreEqual(item.FilePath, filePath))
                    .GroupBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new ClaudeIdeFileDiagnostics(group.Key, group
                        .OrderBy(item => item.Line1).ThenBy(item => item.Column1)
                        .Select(ToDiagnostic).ToList()))
                    .ToList());

        private static ClaudeIdeDiagnostic ToDiagnostic(ProblemItemViewModel item) {
            var startLine = Math.Max(0, item.Line1 - 1);
            var startColumn = Math.Max(0, item.Column1 - 1);
            var endLine = item.EndLine1 > 0 ? item.EndLine1 - 1 : startLine;
            var endColumn = item.EndColumn1 > 0 ? item.EndColumn1 - 1 : startColumn;
            return new ClaudeIdeDiagnostic(item.Message, item.Severity switch {
                ProblemSeverity.Error => ClaudeIdeSeverity.Error,
                ProblemSeverity.Warning => ClaudeIdeSeverity.Warning,
                ProblemSeverity.Information => ClaudeIdeSeverity.Info,
                _ => ClaudeIdeSeverity.Hint,
            }, startLine, startColumn, endLine, endColumn, item.SourceLabel, item.HasCode ? item.Code : null);
        }

        public Task OpenFileAsync(string filePath, string? startText, string? endText) => OnUi(async () => {
            await window.OpenFileInNewEditorTabAsync(filePath);
            if (string.IsNullOrEmpty(startText) || window._activeEditorTab is not { IsRealized: true } tab
                || !FilePathRelations.AreEqual(tab.PeekFilePath ?? "", filePath))
                return true;
            var text = tab.Control.Text;
            var start = text.IndexOf(startText, StringComparison.Ordinal);
            if (start < 0)
                return true;
            var end = start + startText.Length;
            if (!string.IsNullOrEmpty(endText) && text.IndexOf(endText, end, StringComparison.Ordinal) is var found and >= 0)
                end = found + endText.Length;
            var (startLine, startColumn) = LineColumnOf(text, start);
            var (endLine, endColumn) = LineColumnOf(text, end);
            tab.Control.SelectRange(startLine, startColumn, endLine, endColumn);
            return true;
        });

        public Task<bool?> IsDirtyAsync(string filePath) => OnUi<bool?>(() =>
            window.FindEditorTabByPath(filePath) is { } tab ? tab.IsRealized && tab.Control.IsModified : null);

        public Task<bool> SaveAsync(string filePath) => OnUi(async () => {
            if (window.FindEditorTabByPath(filePath) is not { IsRealized: true } tab)
                return false;
            await window._editor.SaveFileAsync(tab.Control);
            return true;
        });

        public Task<ClaudeIdeDiffDecision> ShowProposedEditAsync(
            string tabName, string filePath, string newContents, CancellationToken cancellationToken)
            => OnUi(() => window._vm.ClaudeProposal.Enqueue(tabName, filePath, newContents, cancellationToken));

        private static (int Line, int Column) LineColumnOf(string text, int offset) {
            var line = 0;
            var lineStart = 0;
            for (var i = 0; i < offset && i < text.Length; i++) {
                if (text[i] == '\n') {
                    line++;
                    lineStart = i + 1;
                }
            }
            return (line, offset - lineStart);
        }
    }
}
