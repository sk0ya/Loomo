using sk0ya.Loomo.Core.Viewers;
using sk0ya.Loomo.Services.Viewers;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// Viewer のタブを作る（§44.2）。置き場の Viewer の id からも、パスからも、保存した鍵からも同じ形で作る——ペインと
/// 切り離した窓が同じものを使う。どう見せるかは <see cref="ViewerTargets.Classify"/> が決める。
/// </summary>
internal sealed class ViewerTabFactory(
    ViewerStore store, ViewerRoomState state, ViewerRoomApi api, IEditorSupportViewFactory viewFactory,
    EditorSupportResolver resolver, EditorSupportPipeline pipeline, LoomoSettings settings, IWorkspaceService workspace,
    Func<string, Task> openUrl, Func<string, string?, Task> linkClicked, Dispatcher dispatcher,
    Func<VimEditorControl> createEditor, Action<string> openInEditor, Func<WebView2CompositionControl> createBrowserView)
{
    public ViewerStore Store => store;
    public ViewerRoomState State => state;

    public ViewerTarget Classify(string path) => ViewerTargets.Classify(path, resolver, store);
    public string Describe(ViewerTarget target) => ViewerTargets.Describe(target, store);

    /// <summary>置き場の Viewer（<paramref name="target"/> を渡すと、そのファイルを見せるタブ）。</summary>
    public ViewerTabHost ForViewer(string id, Panel host, string? target = null)
        => new(id, store, state, api, viewFactory, host, dispatcher, openUrl, target);

    /// <summary>保存した鍵から（復元）。プログラムは起動せず、Viewer がまだ無いファイルは作らずに待つ。</summary>
    public IViewerTab? ForKey(string key, Panel host)
    {
        if (ViewerPath.TryParseUrlKey(key, out var url))
            return ForUrl(url, host);
        if (ViewerPath.TryParseKey(key, out var path))
            return ForPath(path, host, fresh: false);
        return ViewerManifest.IsValidId(key) ? ForViewer(key, host) : null;
    }

    public ViewerWebTab ForUrl(string url, Panel host)
        => new(url, viewFactory, createBrowserView, host, dispatcher, openUrl);

    /// <summary>「Claude Code に見せ方を作ってもらう」：そのファイルの種類の Viewer をいま作る。</summary>
    public ViewerGeneratingTab Generate(string path, Panel host)
    {
        var id = ViewerPath.IdFor(path, Directory.Exists(path));
        state.RecordOpened(id, DateTime.Today);
        return new ViewerGeneratingTab(path, id, ViewerGeneratingTab.StartMode.Now, store, host, dispatcher);
    }

    /// <param name="fresh">人間がいまパスを渡した（プログラムを起動する）。</param>
    public IViewerTab ForPath(string path, Panel host, bool fresh)
    {
        var target = Classify(path);
        switch (target.Kind)
        {
            case ViewerTargetKind.Page:
                return new ViewerPageTab(path, target.Page!, viewFactory, host, dispatcher, openUrl);
            case ViewerTargetKind.Preview:
                return new ViewerPreviewTab(path, resolver, pipeline, viewFactory, settings, workspace, linkClicked, host, dispatcher);
            case ViewerTargetKind.Program:
                return new ViewerProgramTab(path, fresh, host, dispatcher);
            case ViewerTargetKind.Text:
                return new ViewerEditorTab(path, createEditor, openInEditor, host, dispatcher);
            case ViewerTargetKind.Web:
                return ForUrl(target.Path, host);
            case ViewerTargetKind.Generated when target.ViewerId is { } id:
                if (fresh)
                    state.RecordOpened(id, DateTime.Today);
                // 作ってあればそれで。無いのはフォルダーだけ——勝手には作らず、作るかを聞く。
                return store.Find(id).Manifest is not null
                    ? ForViewer(id, host, path)
                    : new ViewerGeneratingTab(path, id, ViewerGeneratingTab.StartMode.Wait, store, host, dispatcher);
            default:
                return new ViewerMissingTab(path, host, dispatcher);
        }
    }

    /// <summary>「作り直す」：一言を聞いてから、同じ id の Viewer を Claude Code に直させる。</summary>
    public ViewerGeneratingTab Remake(ViewerTabHost tab, Panel host)
        => new(tab.TargetPath!, tab.Id, ViewerGeneratingTab.StartMode.AskNote, store, host, dispatcher);
}

/// <summary>開いていたパスがもう無い（復元したタブ）。消えたことを言い、閉じる道を出す。</summary>
internal sealed class ViewerMissingTab : ViewerTabBase
{
    public ViewerMissingTab(string targetPath, Panel host, Dispatcher dispatcher)
        : base(targetPath, host, dispatcher)
        => SetStatus(ViewerTabStatus.Broken);

    public override UIElement? BuildCard()
    {
        var panel = ViewerCards.Panel();
        panel.Children.Add(ViewerCards.Line($"「{Title}」が見つかりません", "Fs14", "Fg", bold: true));
        panel.Children.Add(ViewerCards.Line("移動したか消えたようです。", "Fs12", "FgDim"));
        panel.Children.Add(ViewerCards.Mono(TargetPath!, "Fs11", "FgDim", top: 8));
        panel.Children.Add(ViewerCards.Buttons(ViewerCards.Button("タブを閉じる", "SecondaryButton", RequestClose)));
        return panel;
    }

    protected override void OnShownChanged() { }
    protected override void Unmount() { }
}
