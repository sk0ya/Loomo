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
        Action<EditorTab> adoptEditorTab)
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

        var synchronization = new EditorMirrorSynchronization(source, mirror);
        var title = string.IsNullOrWhiteSpace(source.FilePath) ? "Untitled" : Path.GetFileName(source.FilePath!);
        return new DetachedItem(DetachKind.EditorMirror, title, mirror, getFileIcon(source.FilePath), dispose: () =>
        {
            synchronization.Dispose();
            mirror.Dispose();
        })
        {
            // 帯へ戻すときは追従を解除して、独立したタブとして迎える。
            Return = new DetachReturn(TabEntryKind.Editor, () =>
            {
                synchronization.Dispose();
                adoptEditorTab(mirrorTab);
            })
        };
    }

    private sealed class EditorMirrorSynchronization : IDisposable
    {
        private readonly VimEditorControl _source;
        private readonly VimEditorControl _mirror;
        private bool _syncing;

        public EditorMirrorSynchronization(VimEditorControl source, VimEditorControl mirror)
        {
            _source = source;
            _mirror = mirror;
            _source.BufferChanged += OnSourceBufferChanged;
            _mirror.BufferChanged += OnMirrorBufferChanged;
        }

        public void Dispose()
        {
            _source.BufferChanged -= OnSourceBufferChanged;
            _mirror.BufferChanged -= OnMirrorBufferChanged;
        }

        private void OnSourceBufferChanged(object? sender, EventArgs e) => Sync(_source, _mirror);
        private void OnMirrorBufferChanged(object? sender, EventArgs e) => Sync(_mirror, _source);

        private void Sync(VimEditorControl from, VimEditorControl to)
        {
            if (_syncing || string.Equals(to.Text, from.Text, StringComparison.Ordinal))
                return;
            _syncing = true;
            try
            {
                var caret = to.Caret;
                to.SetText(from.Text);
                try { to.NavigateTo(caret.Line, caret.Column); } catch { /* 本文が縮んだ場合は内部で範囲調整 */ }
            }
            finally { _syncing = false; }
        }
    }
}
