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

    private void InitializeLoomoMcp() {
        if (!_settings.ClaudeCodeIdeIntegration)
            return;
        try {
            _loomoMcp = new LoomoMcpServer(new LoomoRoomHost(this));
            _loomoMcp.Start();
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
        try { Clipboard.SetText(LoomoMcpServer.RegistrationCommand); }
        catch { ToastService.Error("クリップボードへコピーできませんでした"); return; }
        ToastService.Info(_loomoMcp is null
            ? "登録のコマンドをコピーしました（いまは MCP サーバーが動いていません。設定の Claude Code 連携を確かめてください）"
            : "登録のコマンドをコピーしました。Loomo のターミナル（PowerShell）に貼って 1 回だけ実行してください。"
              + "新しく開いたターミナルのタブで起動した claude から使えます");
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
