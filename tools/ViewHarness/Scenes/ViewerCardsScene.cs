using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Editor.Controls;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.Services.Viewers;

namespace ViewHarness.Scenes;

/// <summary>Viewer ペイン（§44.2「パスから開く」）のカード：開いていないとき・パスを聞くとき・プログラムをまだ起動していないとき・
/// 見せ方がまだ無いフォルダー・作り直しの一言・消えたパス・テキストをエディタで読むときの帯。実物の <see cref="ViewerPaneView"/> と
/// タブを使い、EditorSupport や WebView2 の要るものは渡さない（カードは使わない）。置き場は一時フォルダー（2026-11-21）。</summary>
public sealed class ViewerCardsScene : IScene
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "loomo-vh-viewer-" + Guid.NewGuid().ToString("N"));
    private Grid? _root;
    private ViewerStore? _store;

    public string Name => "viewer-cards";
    public string Description => "Viewer ペインのカード（空・パスを開く・プログラム・フォルダー・作り直し・消えたパス・テキストの帯）";
    public Size Size => new(560, 420);

    public FrameworkElement Build()
    {
        Directory.CreateDirectory(_temp);
        _store = new ViewerStore(Path.Combine(_temp, "viewers"));
        _root = new Grid();
        _root.Children.Add(Pane());
        return _root;
    }

    private ViewerPaneView Pane()
    {
        var state = new ViewerRoomState(Path.Combine(_temp, "state.json"));
        var factory = new ViewerTabFactory(_store!, state, null!, null!, null!, null!, null!, null!,
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask, Dispatcher.CurrentDispatcher,
            () => new VimEditorControl(new VimEditorControlOptions()), _ => { }, null!);
        var view = new ViewerPaneView();
        view.Configure(factory);
        return view;
    }

    private void ShowCard(IViewerTab tab)
    {
        _root!.Children.Clear();
        var card = new Border { Child = new ScrollViewer { Content = tab.BuildCard() } };
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        _root.Children.Add(card);
    }

    private string Put(string name, string text)
    {
        var path = Path.Combine(_temp, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public IEnumerable<SceneStep> Steps(FrameworkElement root) =>
    [
        new SceneStep("open-card", () =>
        {
            var pane = (ViewerPaneView)_root!.Children[0];
            pane.ShowOpenCard(@"C:\Users\someone\Documents\とても長い名前のフォルダー\さらに長いサブフォルダー\report-final-v3.weird");
        }),
        new SceneStep("program-idle", () => ShowCard(new ViewerProgramTab(
            @"C:\Program Files\Some Vendor\Very Long Application Name\bin\x64\application-launcher.exe", start: false, new Grid(), Dispatcher.CurrentDispatcher))),
        new SceneStep("folder-wait", () => ShowCard(new ViewerGeneratingTab(
            Path.Combine(_temp, "photos"), "file-folder", ViewerGeneratingTab.StartMode.Wait, _store!, new Grid(), Dispatcher.CurrentDispatcher))),
        new SceneStep("remake-ask", () => ShowCard(new ViewerGeneratingTab(
            Put("data.weird", "a,b"), "file-weird", ViewerGeneratingTab.StartMode.AskNote, _store!, new Grid(), Dispatcher.CurrentDispatcher))),
        new SceneStep("missing", () => ShowCard(new ViewerMissingTab(
            @"D:\gone\somewhere\that-file-was-moved.weird", new Grid(), Dispatcher.CurrentDispatcher))),
        new SceneStep("text-band", () =>
        {
            _root!.Children.Clear();
            var host = new Grid();
            _root.Children.Add(host);
            var tab = new ViewerEditorTab(Put("notes.weird", "1 行目\n2 行目：見せ方の決まっていないファイル\n"),
                () => new VimEditorControl(new VimEditorControlOptions()), _ => { }, host, Dispatcher.CurrentDispatcher);
            tab.SetVisible(true);
        }),
    ];
}
