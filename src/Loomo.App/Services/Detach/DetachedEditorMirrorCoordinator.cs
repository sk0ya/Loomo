using System.Windows.Media;
using Editor.Controls;
using sk0ya.Loomo.App.Detach;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>別窓の Editor 複製と元タブの双方向同期・解除をまとめる。</summary>
internal static class DetachedEditorMirrorCoordinator
{
    public static DetachedItem? TryCreate(
        IReadOnlyList<EditorTab> editorTabs,
        Guid sourceTabId,
        Func<EditorTab> createEditorTab,
        Action<VimEditorControl, string> loadFile,
        Func<string?, ImageSource?> getFileIcon,
        Action<EditorTab> adoptEditorTab,
        EditorDocumentEvents events)
    {
        var sourceTab = editorTabs.FirstOrDefault(tab => tab.Id == sourceTabId);
        if (sourceTab is null)
            return null;

        var source = sourceTab.Control;
        var mirrorTab = createEditorTab();
        var mirror = mirrorTab.Control;
        if (!string.IsNullOrWhiteSpace(source.FilePath) && File.Exists(source.FilePath) && !source.IsModified)
            loadFile(mirror, source.FilePath);
        else
            mirror.SetText(source.Text);

        var synchronization = new EditorTextMirror(source, mirror, events);
        // 片方がディスクから読み直された（ブランチ切替・一括置換）：LoadFile は BufferChanged を出さないので、
        // もう片方へ読み込みとして写す。写さずに古い側で打つと、読み直した本文を古い本文で上書きする。
        void OnLoaded(VimEditorControl loaded)
        {
            var other = ReferenceEquals(loaded, source) ? mirror : ReferenceEquals(loaded, mirror) ? source : null;
            if (other is not null && !string.Equals(other.Text, loaded.Text, StringComparison.Ordinal))
                other.SetText(loaded.Text);
        }
        events.Loaded += OnLoaded;
        void Unsync()
        {
            events.Loaded -= OnLoaded;
            synchronization.Dispose();
        }
        var title = string.IsNullOrWhiteSpace(source.FilePath) ? "Untitled" : Path.GetFileName(source.FilePath!);
        return new DetachedItem(DetachKind.EditorMirror, title, mirror, getFileIcon(source.FilePath), dispose: () =>
        {
            Unsync();
            mirror.Dispose();
        })
        {
            // 帯へ戻すときは追従を解除して、独立したタブとして迎える。
            Return = new DetachReturn(TabEntryKind.Editor, () =>
            {
                Unsync();
                adoptEditorTab(mirrorTab);
            })
        };
    }
}
