using Editor.Controls;
using Microsoft.Web.WebView2.Wpf;
using sk0ya.Loomo.App.Detach;
using sk0ya.Loomo.App.Views;
using Terminal.Tabs;

namespace sk0ya.Loomo.App.Services;

/// <summary>切り離し項目の保存形式への変換と、保存状態から復元する項目種別を調整する。</summary>
internal static class DetachedItemStateCoordinator
{
    public static DetachedItemSnapshot? Capture(DetachedItem item)
    {
        var snapshot = new DetachedItemSnapshot { Kind = item.Kind.ToString() };
        switch (item.Content)
        {
            case VimEditorControl editor:
                snapshot.FilePath = editor.FilePath;
                snapshot.Text = editor.IsModified || string.IsNullOrWhiteSpace(editor.FilePath) ? editor.Text : null;
                snapshot.IsModified = editor.IsModified;
                break;
            case TerminalTabView terminal:
                snapshot.WorkingDirectory = terminal.WorkingDirectory;
                break;
            case WebView2CompositionControl browser:
                snapshot.Url = browser.TryUrl();
                break;
            // 切り離したブラウザの Grid は、WebView2 が生成される前の URL も保持する。
            case Panel host when host.Children.OfType<WebView2CompositionControl>().FirstOrDefault() is { } hosted:
                var address = SpinoffBrowserAddress.Of(host);
                address?.Note(hosted.TryUrl());
                snapshot.Url = hosted.TryUrl() ?? address?.Value;
                break;
            case DetachedEditorSupportView preview:
                snapshot.FilePath = preview.SourceFilePath;
                break;
            default:
                return null;
        }
        return snapshot;
    }

    public static DetachedItem? Restore(
        DetachedItemSnapshot snapshot,
        Func<string, Guid?> findEditorTab,
        Func<Guid, DetachedItem?> createEditorMirror,
        Func<DetachedItemSnapshot, DetachedItem?> createEditorMove,
        Func<string, DetachedItem?> createEditorSupportMirror,
        Func<string?, DetachedItem> createTerminalSpinoff,
        Func<string?, DetachedItem> createBrowserSpinoff)
    {
        if (!Enum.TryParse<DetachKind>(snapshot.Kind, out var kind))
            return null;

        if (kind == DetachKind.EditorMirror && !string.IsNullOrWhiteSpace(snapshot.FilePath) &&
            findEditorTab(snapshot.FilePath) is { } sourceTabId)
            return createEditorMirror(sourceTabId);

        if (kind is DetachKind.EditorMirror or DetachKind.EditorMove)
            return createEditorMove(snapshot);

        if (kind == DetachKind.EditorSupportMirror && !string.IsNullOrWhiteSpace(snapshot.FilePath))
            return createEditorSupportMirror(snapshot.FilePath);

        if (kind is DetachKind.TerminalSpinoff or DetachKind.TerminalMove)
            return createTerminalSpinoff(snapshot.WorkingDirectory);

        if (kind == DetachKind.BrowserSpinoff)
            return createBrowserSpinoff(snapshot.Url);

        return null;
    }
}
