using sk0ya.Loomo.Core.Harness;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.App.Views;
/// <summary>
/// ShellWindow: 部屋を Claude から使う（§43.6 D）。Loomo が MCP サーバーとして、Claude が<b>人間の前に物を置く</b>道具を出す
/// ——ファイルのこの行・URL・いまの頼みごとの差分。
/// <para><b>線引き</b>（主役は人間、§38.1）：置くだけ。見えるターミナルには打たない・タブを閉じない・配置を変えない
/// （隠れているペインを出すために並べ替えない）・<b>フォーカスを奪わない</b>（人間は Claude に向かって打っている最中かもしれない。
/// 開いたあと、元のペインへ戻す）。置いたことは通知で知らせる。開けるのはワークスペースの中のファイルと http(s) の URL だけ。</para>
/// サーバーは別スレッドで動くので、ホストの答えはどれも Dispatcher を通す。
/// </summary>
public partial class ShellWindow {
    private LoomoMcpServer? _loomoMcp;

    /// <summary>次の頼みごとに添える知らせ（§45.6 P3・§45.4 P1）。サーバーのスレッドからも読むので、待ち行列はスレッドセーフ。</summary>
    private readonly ClaudeNoteQueue _claudeNotes = new();

    /// <summary>UserPromptSubmit の答え：このセッションの知らせを添える（1 件は 2 回まで。返事が橋に届かなかった回の保険に、重複を許す）。
    /// 無ければ null（フックは何も書かない）。</summary>
    private System.Text.Json.Nodes.JsonObject? ClaudeNoteForPrompt(System.Text.Json.Nodes.JsonObject hookEvent) {
        if ((string?)hookEvent["session_id"] is not { Length: > 0 } session)
            return null;
        _continuations.TryRemove(session, out _);   // 新しい頼みごと：続けさせた回数を数え直す（§45.6）
        var notes = _claudeNotes.Take(session);
        if (notes.Count == 0)
            return null;
        Dispatcher.BeginInvoke(() => {
            if (_activeEditorTab is { IsRealized: true } active)
                _pullRequestComments.RefreshExtraMarkers(active.Control);   // 渡した指摘の印は消す
        });
        return new System.Text.Json.Nodes.JsonObject {
            ["hookSpecificOutput"] = new System.Text.Json.Nodes.JsonObject {
                ["hookEventName"] = "UserPromptSubmit",
                ["additionalContext"] = ClaudeNoteQueue.FormatContext(notes),
            },
        };
    }

    private void InitializeLoomoMcp() {
        if (!_settings.ClaudeCodeIdeIntegration)
            return;
        try {
            _loomoMcp = new LoomoMcpServer(new LoomoRoomHost(this));
            _loomoMcp.Start();
            // Claude Code が起こす橋を、bin の外へ写しておく（起動を待たせない）。
            _ = Task.Run(() => LoomoMcpBridgeInstaller.Install());
        } catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or UnauthorizedAccessException) {
            System.Diagnostics.Trace.WriteLine($"Loomo の MCP サーバーを開始できませんでした: {ex.Message}");
            _loomoMcp?.Dispose();
            _loomoMcp = null;
        }
    }

    private void DisposeLoomoMcp() {
        _loomoMcp?.Dispose();
        _loomoMcp = null;
    }

    /// <summary>Claude Code への登録はユーザーが自分で 1 回だけ打つ（ユーザーの設定 <c>~/.claude.json</c> に定義が 1 つ入る）。
    /// Loomo は黙って書かない——コマンドをクリップボードへ渡すところまで。</summary>
    private void CopyLoomoMcpRegistrationCommand() {
        // 登録するのは橋の場所（%LOCALAPPDATA% へ写したもの）。まだ写せていなければここで写す。
        if (!LoomoMcpBridgeInstaller.Install()) {
            ToastService.Error("Claude Code とつなぐ橋（sk0ya.Loomo.McpBridge.exe）を用意できませんでした");
            return;
        }
        try { Clipboard.SetText(LoomoMcpBridgeInstaller.RegistrationCommand(LoomoMcpBridgeInstaller.InstalledExe)); }
        catch { ToastService.Error("クリップボードへコピーできませんでした"); return; }
        ToastService.Info(_loomoMcp is null
            ? "登録のコマンドをコピーしました（いまは MCP サーバーが動いていません。設定の Claude Code 連携を確かめてください）"
            : "登録のコマンドをコピーしました。ターミナル（PowerShell）に貼って 1 回だけ実行してください。"
              + "そのあと起動した claude から使えます（開いてあるタブでも）");
    }

    /// <summary>操作のあと、フォーカスを元のペインへ戻す（Claude が物を置いても、人間の打鍵の行き先は変えない）。</summary>
    private async Task<T> WithoutStealingFocusAsync<T>(Func<Task<T>> act) {
        var before = _focusedRegion?.Pane;
        var result = await act();
        if (before is { } pane && _focusedRegion?.Pane != pane)
            FocusPane(pane);
        return result;
    }

    private string HiddenNote(PaneKind kind)
        => IsPaneMaterialized(kind) ? "" : "（そのペインはいま出ていません）";

    private sealed class LoomoRoomHost(ShellWindow window) : ILoomoRoomHost {
        private Task<T> OnUi<T>(Func<Task<T>> run) => window.Dispatcher.InvokeAsync(run).Task.Unwrap();

        public Task<LoomoRoomResult> ShowFileAsync(string path, int line) => OnUi(async () => {
            if (window.ResolveWorkspaceFile(path) is not { } full)
                return new LoomoRoomResult(false, "That file is not in the user's workspace (or does not exist).");
            await window.WithoutStealingFocusAsync(async () => {
                await window.OpenPathInEditorAsync(full, line, 1);
                return true;
            });
            var where = line > 0 ? $"{window._workspace.ToDisplayPath(full)}:{line}" : window._workspace.ToDisplayPath(full);
            ToastService.Info($"Claude が {where} をエディタに出しました{window.HiddenNote(PaneKind.Editor)}");
            return new LoomoRoomResult(true, $"Shown in the editor pane: {where}");
        });

        public Task<LoomoRoomResult> ShowUrlAsync(string url) => OnUi(async () => {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return new LoomoRoomResult(false, "Only http:// and https:// URLs can be shown.");
            // 人間が見ているページを置き換えない——新しいタブで。
            await window.WithoutStealingFocusAsync(async () => {
                await window.CreateBrowserTabAsync(uri.AbsoluteUri);
                window.SaveActiveWorkspaceSnapshot();
                return true;
            });
            ToastService.Info($"Claude が {uri.Host} をブラウザの新しいタブに出しました{window.HiddenNote(PaneKind.Browser)}");
            return new LoomoRoomResult(true, $"Opened in a new tab of the browser pane: {uri.AbsoluteUri}");
        });

        // 面（§44.2）：書き方を読む・出す。ファイルを書くのは Claude Code 自身。
        public string FaceGuide() => sk0ya.Loomo.Services.Faces.FaceGuide.Text(window._faceStore);

        // 動き（§44.3）：見どころの書き方を読む。書くのは Claude Code 自身、許すのは人間。
        public string MotionGuide() => sk0ya.Loomo.Services.Motion.MotionGuide.Text(window._motion.Store, window._workspace.PrimaryFolder);

        /// <summary>
        /// この Loomo の客か（§45.3 P0b・P0c）。環境変数のタブがこの Loomo のものならそれ。無い・違う（常駐で繋ぎ直したタブ・
        /// 別の Loomo のタブ）なら、claude の祖先の PID の中に自分のタブのシェルを探す。シェルの表は並行に書かれる辞書なので、
        /// UI スレッドを待たない。
        /// <para><b>タブのシェルを通らない claude は客にしない</b>——Loomo が裏で起こした claude（言いつけ＝§44.4）は祖先に Loomo が
        /// 居るが、人間の画面に物を置かせない約束なので断る（橋は「外」と同じく道具の無い一覧を返す）。</para>
        /// </summary>
        public LoomoCaller? Identify(Guid? tab, IReadOnlyList<int> ancestors) {
            if (tab is { } hinted && window._terminalShells.ContainsKey(hinted))
                return new LoomoCaller(hinted);
            if (ancestors.Count == 0)
                return null;
            var shells = new Dictionary<int, Guid>();
            foreach (var id in window._terminalShells.Keys)
                if (window.TerminalShellProcessId(id) is { } shellPid and > 0)
                    shells.TryAdd(shellPid, id);
            for (var i = 0; i < ancestors.Count; i++)
                if (shells.TryGetValue(ancestors[i], out var id))
                    return new LoomoCaller(id, Nested: CountClaudes(ancestors.Take(i)) >= 2);
            return null;
        }

        /// <summary>シェルより下の祖先のうち claude のプロセスの数（npm で入れた claude は node として動く）。</summary>
        private static int CountClaudes(IEnumerable<int> pids) {
            var count = 0;
            foreach (var pid in pids) {
                try {
                    using var process = System.Diagnostics.Process.GetProcessById(pid);
                    if (process.ProcessName is var name
                        && (name.Equals("claude", StringComparison.OrdinalIgnoreCase) || name.Equals("node", StringComparison.OrdinalIgnoreCase)))
                        count++;
                } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
            }
            return count;
        }

        /// <summary>フック（§45.3 P0a）。いまは前の版と同じ 2 つ（承認待ち・セッションの結び付け）を UI へ渡すだけで、返すものは無い。
        /// claude の中から起こされた claude のフックはタブに結ばない（タブのセッションが <c>claude -p</c> のものに替わってしまう）。</summary>
        public Task<System.Text.Json.Nodes.JsonObject?> HookAsync(LoomoCaller caller, System.Text.Json.Nodes.JsonObject hookEvent) {
            if ((string?)hookEvent["hook_event_name"] == "UserPromptSubmit")
                return Task.FromResult(window.ClaudeNoteForPrompt(hookEvent));
            if ((string?)hookEvent["hook_event_name"] == "Stop")
                return window.ClaudeStopDecisionAsync(caller, hookEvent);
            if (caller.Tab is { } tab && !caller.Nested)
                window.Dispatcher.BeginInvoke(() => window.OnClaudeHook(tab, hookEvent));
            return Task.FromResult<System.Text.Json.Nodes.JsonObject?>(null);
        }

        public Task<LoomoRoomResult> ShowFaceAsync(string id) => OnUi(() => window.ShowFaceFromClaudeAsync(id));

        public Task<LoomoRoomResult> WhatHumanSeesAsync() => window.Dispatcher.InvokeAsync(window.DescribeHumanView).Task;

        public Task<LoomoRoomResult> DebugStateAsync() => window.Dispatcher.InvokeAsync(window.DescribeDebugState).Task;

        public Task<LoomoRoomResult> BrowserErrorsAsync() => window.Dispatcher.InvokeAsync(window.DescribeBrowserErrors).Task;

        public Task<LoomoRoomResult> ShowChangesAsync() => OnUi(async () => {
            var origin = await Task.Run(() => window.ClaudeProvenance.LatestRequest());
            if (origin is null)
                return new LoomoRoomResult(false, "No recent request that changed files was found in this workspace.");
            var files = await window.WithoutStealingFocusAsync(() => window.ShowClaudeRequestDiffAsync(origin));
            if (files == 0)
                return new LoomoRoomResult(false, "The changes of the latest request could not be read from the session record.");
            ToastService.Info($"Claude が頼みごとの差分（{files} ファイル）を Diff に出しました");
            return new LoomoRoomResult(true, $"Shown in the diff pane: {files} file(s) changed by the request \"{origin.PromptHead}\".");
        });
    }

    /// <summary>ワークスペースの中の、いまあるファイルへ解く。相対パスは各フォルダーから探す。外・無いものは null。</summary>
    private string? ResolveWorkspaceFile(string path) {
        try {
            var candidates = Path.IsPathRooted(path)
                ? [Path.GetFullPath(path)]
                : _workspace.Folders.Select(folder => Path.GetFullPath(Path.Combine(folder, path)));
            return candidates.FirstOrDefault(full => _workspace.Contains(full) && File.Exists(full));
        } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
            return null;
        }
    }
}
