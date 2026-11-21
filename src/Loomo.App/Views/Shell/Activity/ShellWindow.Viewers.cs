using System.Text.Json.Nodes;
using sk0ya.Loomo.Core.Viewers;
using sk0ya.Loomo.Services.ClaudeCode;
using sk0ya.Loomo.Services.Viewers;

namespace sk0ya.Loomo.App.Views;
/// <summary>
/// ShellWindow: 生えるViewer（§44.2）。頼むと Claude が小さな HTML の道具を書き、「Viewer」ペインに出る。Loomo は再ビルドしない。
/// ここは部屋の側の受け口——Viewerのページから届く頼み（<see cref="IViewerRoom"/>）と、Claude がViewerを出す道具（MCP の
/// <c>show_viewer</c>）と、人間の入口（パレット・ペインの「＋」）。線引き（口は定義が挙げたものだけ・回せるのは書いてある
/// コマンドだけ・許すのは人間）は <see cref="ViewerRoomApi"/> と <see cref="ViewerTabHost"/> が持つ。
/// </summary>
public partial class ShellWindow {
    private readonly ViewerStore _viewerStore = new();
    private readonly ViewerRoomState _viewerState = new();
    private GitWorkspaceStatus? _viewerGitStatus;
    private ViewerRoomApi? _viewerApi;
    private string? _viewerLastActiveFile;

    private void InitializeViewers(IEditorSupportViewFactory viewFactory) {
        InitializeMotion();
        _viewerApi = new ViewerRoomApi(_viewerStore, _workspace, new ViewerRoom(this),
            [new MotionViewerApi(this), new OrdersViewerApi(this)]);
        // 本体のViewer（動き・言いつけ）を置く。無いとき・版が古いときだけ（§44.3・§44.4）。
        _ = Task.Run(() => BuiltInViewers.Install(_viewerStore));
        DeferPaneContent(ViewerPaneHost, () => {
            var view = new ViewerPaneView();
            view.Configure(_viewerStore, _viewerState, _viewerApi, viewFactory, url => OpenUrlInNewBrowserTabAsync(url));
            view.GrowRequested += (_, _) => CopyViewerGrowPrompt();
            return view;
        });
        // リポジトリの状態が変わった（背景スレッドで鳴る）→ 出ているViewerへ。Viewerを一度も出していなければ何もしない。
        if (_viewerGitStatus is { } status)
            status.RepositoryChanged += (_, root) => Dispatcher.BeginInvoke(DispatcherPriority.Background,
                () => RealizedViewerPane()?.Emit("gitChanged", new JsonObject { ["root"] = root }, ViewerApi.Git));
    }

    /// <summary>作ってあれば「Viewer」ペインの中身（作っていなければ null——出来事を流すためだけに作らない）。</summary>
    private ViewerPaneView? RealizedViewerPane() => ViewerPaneHost.Content as ViewerPaneView;

    /// <summary>前に出ているファイルが変わった（<see cref="OnActiveEditorFileChanged"/> から）。</summary>
    private void NotifyViewersActiveFileChanged(string? path) {
        if (string.Equals(path, _viewerLastActiveFile, StringComparison.OrdinalIgnoreCase))
            return;
        _viewerLastActiveFile = path;
        RealizedViewerPane()?.Emit("activeFileChanged", new JsonObject { ["path"] = path }, ViewerApi.Workspace);
    }

    /// <summary>人間がViewerを開く（パレット・キー）。ペインを出して前へ。</summary>
    private void OpenViewer(string id) {
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Viewer);
        _ = PaneContent<ViewerPaneView>(ViewerPaneHost).ShowViewerAsync(id);
        FocusPane(PaneKind.Viewer);
    }

    /// <summary>「Viewerを生やす」：Claude への頼みごとの下書きをクリップボードへ。Loomo はターミナルに打たない。</summary>
    private void CopyViewerGrowPrompt() {
        const string prompt = "Loomo にViewerを生やして：（ここに、ほしい道具を書く。例：いまポートを握っているプロセスの一覧。行を押すとそのプロセスを止められる）";
        try { Clipboard.SetText(prompt); }
        catch { ToastService.Error("クリップボードへコピーできませんでした"); return; }
        ToastService.Info(_loomoMcp is null
            ? "頼みごとの下書きをコピーしました（Claude Code からViewerを出すには、設定の Claude Code 連携と MCP の登録が要ります）"
            : "頼みごとの下書きをコピーしました。ターミナルの Claude Code に貼って、ほしい道具を書いてください");
    }

    /// <summary>パレットに並べるViewer（置いてあるもの全部）。</summary>
    private IEnumerable<(string Title, Action Open)> ViewerPaletteEntries() {
        foreach (var viewer in _viewerStore.List())
            yield return ($"Viewerを開く：{viewer.Title}", () => OpenViewer(viewer.Id));
    }

    /// <summary>Claude がViewerを出す（MCP の <c>show_viewer</c>）。フォーカスは奪わず、隠れているペインを出すための並べ替えもしない。</summary>
    private async Task<LoomoRoomResult> ShowViewerFromClaudeAsync(string id) {
        var entry = await Task.Run(() => _viewerStore.Find(id));
        if (entry.Manifest is not { } manifest)
            return new LoomoRoomResult(false, entry.Error ?? "The viewer could not be read.");
        var viewer = await WithoutStealingFocusAsync(() => PaneContent<ViewerPaneView>(ViewerPaneHost).ShowViewerAsync(id));
        var hidden = HiddenNote(PaneKind.Viewer);
        if (viewer?.Status == ViewerTabStatus.NeedsApproval) {
            ToastService.Info($"Claude がViewer「{manifest.Title}」を生やしました。Viewerのペインで中身を確かめて許すと開きます{hidden}");
            return new LoomoRoomResult(true,
                $"The viewer \"{manifest.Title}\" is placed in the viewer pane and is waiting for the user's one-time approval of its api and commands. "
                + "It opens as soon as they approve; you do not need to do anything else.");
        }
        if (viewer?.Status != ViewerTabStatus.Running)
            return new LoomoRoomResult(false, viewer?.Error ?? "The viewer could not be shown.");
        ToastService.Info($"Claude がViewer「{manifest.Title}」を出しました{hidden}");
        return new LoomoRoomResult(true, $"Shown in the viewer pane: {manifest.Title}. Saving files in its folder reloads it.");
    }

    /// <summary>Viewerの口のうち画面が要るもの。Viewerのページから届くので、どれも Dispatcher を通す。</summary>
    private sealed class ViewerRoom(ShellWindow window) : IViewerRoom {
        private Task<T> OnUi<T>(Func<Task<T>> run) => window.Dispatcher.InvokeAsync(run).Task.Unwrap();

        public Task<JsonNode?> ActiveFileAsync() => OnUi(() => {
            JsonNode? result = null;
            if (window._activeEditorTab is { IsRealized: true } tab && tab.Control.FilePath is { Length: > 0 } path) {
                var caret = tab.Control.Caret;
                result = new JsonObject { ["path"] = path, ["line"] = caret.Line + 1, ["column"] = caret.Column + 1 };
            }
            return Task.FromResult(result);
        });

        // Viewerを押したのは人間なので、開いた先へフォーカスが移ってよい（Claude が置くとき＝MCP とはここが違う）。
        public Task<bool> OpenFileAsync(string path, int line) => OnUi(async () => {
            if (!window._workspace.Contains(path) || !File.Exists(path))
                return false;
            await window.OpenPathInEditorAsync(path, line, 1);
            return true;
        });

        public Task<bool> OpenUrlAsync(string url) => OnUi(async () => {
            await window.OpenUrlInNewBrowserTabAsync(url);
            return true;
        });

        public async Task<JsonNode> GitStatusAsync() {
            var repositories = new JsonArray();
            if (window._viewerGitStatus is not { } status)
                return repositories;
            // 読んであるものを引く。まだ読んでいないリポジトリだけ、共有の読み手に頼む（git を別に起こさない・§40）。
            var infos = await Task.Run(() => status.Catalog.Repositories);
            foreach (var info in infos) {
                var state = status.Cached(info.Root) ?? await Task.Run(() => status.Load(info.Root, fresh: false, CancellationToken.None));
                var changed = new JsonArray();
                foreach (var (path, kind) in state.FileStatuses.Take(5000))
                    changed.Add(new JsonObject { ["path"] = path, ["kind"] = kind.ToString() });
                repositories.Add(new JsonObject {
                    ["root"] = state.Root,
                    ["name"] = info.Name,
                    ["branch"] = state.Branch,
                    ["ahead"] = state.Ahead,
                    ["behind"] = state.Behind,
                    ["changed"] = changed,
                });
            }
            return repositories;
        }

        public void Toast(string text) => window.Dispatcher.BeginInvoke(() => ToastService.Info(text));
    }
}
