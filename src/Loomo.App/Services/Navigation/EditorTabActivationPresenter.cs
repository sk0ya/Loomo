using System.Windows.Threading;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Services;

/// <summary>エディタタブ切替後に共有ステータスバーを同期する。</summary>
internal sealed class EditorTabActivationPresenter
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<EditorTab?> _activeTab;

    public EditorTabActivationPresenter(
        Dispatcher dispatcher,
        Func<EditorTab?> activeTab)
    {
        _dispatcher = dispatcher;
        _activeTab = activeTab;
    }

    public void OnActivated(EditorTab tab) => SyncSharedStatusBar(tab);

    /// <summary>EditorSharedStatusBarは全エディタで共有され、どのコントロールも内容を書き込める。
    /// タブ切替後、裏タブの再ペアレントやレイアウトが古い内容を上書きするため、Background優先度でもう一度同期する。</summary>
    private void SyncSharedStatusBar(EditorTab tab)
    {
        if (!tab.IsRealized)
            return;
        tab.Control.SyncStatusBar();
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (ReferenceEquals(_activeTab(), tab) && tab.IsRealized)
                tab.Control.SyncStatusBar();
        }));
    }
}
