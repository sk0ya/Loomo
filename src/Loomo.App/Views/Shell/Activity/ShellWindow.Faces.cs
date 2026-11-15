using System.Text.Json.Nodes;
using sk0ya.Loomo.Core.Faces;
using sk0ya.Loomo.Services.ClaudeCode;
using sk0ya.Loomo.Services.Faces;

namespace sk0ya.Loomo.App.Views;
/// <summary>
/// ShellWindow: 生える面（§44.2）。頼むと Claude が小さな HTML の道具を書き、「面」ペインに出る。Loomo は再ビルドしない。
/// ここは部屋の側の受け口——面のページから届く頼み（<see cref="IFaceRoom"/>）と、Claude が面を出す道具（MCP の
/// <c>show_face</c>）と、人間の入口（パレット・ペインの「＋」）。線引き（口は定義が挙げたものだけ・回せるのは書いてある
/// コマンドだけ・許すのは人間）は <see cref="FaceRoomApi"/> と <see cref="FaceTabHost"/> が持つ。
/// </summary>
public partial class ShellWindow {
    private readonly FaceStore _faceStore = new();
    private readonly FaceRoomState _faceState = new();
    private GitWorkspaceStatus? _faceGitStatus;
    private FaceRoomApi? _faceApi;
    private string? _faceLastActiveFile;

    private void InitializeFaces(IEditorSupportViewFactory viewFactory) {
        InitializeMotion();
        _faceApi = new FaceRoomApi(_faceStore, _workspace, new FaceRoom(this),
            [new MotionFaceApi(this), new OrdersFaceApi(this)]);
        // 本体の面（動き・言いつけ）を置く。無いとき・版が古いときだけ（§44.3・§44.4）。
        _ = Task.Run(() => BuiltInFaces.Install(_faceStore));
        DeferPaneContent(FacePaneHost, () => {
            var view = new FacePaneView();
            view.Configure(_faceStore, _faceState, _faceApi, viewFactory, url => OpenUrlInNewBrowserTabAsync(url));
            view.GrowRequested += (_, _) => CopyFaceGrowPrompt();
            return view;
        });
        // リポジトリの状態が変わった（背景スレッドで鳴る）→ 出ている面へ。面を一度も出していなければ何もしない。
        if (_faceGitStatus is { } status)
            status.RepositoryChanged += (_, root) => Dispatcher.BeginInvoke(DispatcherPriority.Background,
                () => RealizedFacePane()?.Emit("gitChanged", new JsonObject { ["root"] = root }, FaceApi.Git));
    }

    /// <summary>作ってあれば「面」ペインの中身（作っていなければ null——出来事を流すためだけに作らない）。</summary>
    private FacePaneView? RealizedFacePane() => FacePaneHost.Content as FacePaneView;

    /// <summary>前に出ているファイルが変わった（<see cref="OnActiveEditorFileChanged"/> から）。</summary>
    private void NotifyFacesActiveFileChanged(string? path) {
        if (string.Equals(path, _faceLastActiveFile, StringComparison.OrdinalIgnoreCase))
            return;
        _faceLastActiveFile = path;
        RealizedFacePane()?.Emit("activeFileChanged", new JsonObject { ["path"] = path }, FaceApi.Workspace);
    }

    /// <summary>人間が面を開く（パレット・キー）。ペインを出して前へ。</summary>
    private void OpenFace(string id) {
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Face);
        _ = PaneContent<FacePaneView>(FacePaneHost).ShowFaceAsync(id);
        FocusPane(PaneKind.Face);
    }

    /// <summary>「面を生やす」：Claude への頼みごとの下書きをクリップボードへ。Loomo はターミナルに打たない。</summary>
    private void CopyFaceGrowPrompt() {
        const string prompt = "Loomo に面を生やして：（ここに、ほしい道具を書く。例：いまポートを握っているプロセスの一覧。行を押すとそのプロセスを止められる）";
        try { Clipboard.SetText(prompt); }
        catch { ToastService.Error("クリップボードへコピーできませんでした"); return; }
        ToastService.Info(_loomoMcp is null
            ? "頼みごとの下書きをコピーしました（Claude Code から面を出すには、設定の Claude Code 連携と MCP の登録が要ります）"
            : "頼みごとの下書きをコピーしました。ターミナルの Claude Code に貼って、ほしい道具を書いてください");
    }

    /// <summary>パレットに並べる面（置いてあるもの全部）。</summary>
    private IEnumerable<(string Title, Action Open)> FacePaletteEntries() {
        foreach (var face in _faceStore.List())
            yield return ($"面を開く：{face.Title}", () => OpenFace(face.Id));
    }

    /// <summary>Claude が面を出す（MCP の <c>show_face</c>）。フォーカスは奪わず、隠れているペインを出すための並べ替えもしない。</summary>
    private async Task<LoomoRoomResult> ShowFaceFromClaudeAsync(string id) {
        var entry = await Task.Run(() => _faceStore.Find(id));
        if (entry.Manifest is not { } manifest)
            return new LoomoRoomResult(false, entry.Error ?? "The face could not be read.");
        var face = await WithoutStealingFocusAsync(() => PaneContent<FacePaneView>(FacePaneHost).ShowFaceAsync(id));
        var hidden = HiddenNote(PaneKind.Face);
        if (face?.Status == FaceTabStatus.NeedsApproval) {
            ToastService.Info($"Claude が面「{manifest.Title}」を生やしました。面のペインで中身を確かめて許すと開きます{hidden}");
            return new LoomoRoomResult(true,
                $"The face \"{manifest.Title}\" is placed in the face pane and is waiting for the user's one-time approval of its api and commands. "
                + "It opens as soon as they approve; you do not need to do anything else.");
        }
        if (face?.Status != FaceTabStatus.Running)
            return new LoomoRoomResult(false, face?.Error ?? "The face could not be shown.");
        ToastService.Info($"Claude が面「{manifest.Title}」を出しました{hidden}");
        return new LoomoRoomResult(true, $"Shown in the face pane: {manifest.Title}. Saving files in its folder reloads it.");
    }

    /// <summary>面の口のうち画面が要るもの。面のページから届くので、どれも Dispatcher を通す。</summary>
    private sealed class FaceRoom(ShellWindow window) : IFaceRoom {
        private Task<T> OnUi<T>(Func<Task<T>> run) => window.Dispatcher.InvokeAsync(run).Task.Unwrap();

        public Task<JsonNode?> ActiveFileAsync() => OnUi(() => {
            JsonNode? result = null;
            if (window._activeEditorTab is { IsRealized: true } tab && tab.Control.FilePath is { Length: > 0 } path) {
                var caret = tab.Control.Caret;
                result = new JsonObject { ["path"] = path, ["line"] = caret.Line + 1, ["column"] = caret.Column + 1 };
            }
            return Task.FromResult(result);
        });

        // 面を押したのは人間なので、開いた先へフォーカスが移ってよい（Claude が置くとき＝MCP とはここが違う）。
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
            if (window._faceGitStatus is not { } status)
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
