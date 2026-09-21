using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Services;

/// <summary>EditorSupport のアウトライン表示を作り、行・ファイル・LSP操作を結び付ける。</summary>
internal sealed class EditorSupportOutlinePresenter(
    EditorSupportController controller,
    Action<int?, int, bool> focusSource,
    Func<string, int, int, bool, Task> openPath,
    Action installLsp,
    Action openLspSettings,
    Action<string> openDocs)
{
    public CodeOutlineView EnsureView()
    {
        if (controller.OutlineView is { } existing)
            return existing;

        var view = new CodeOutlineView();
        view.SourceLocationActivated += (_, e) =>
            focusSource(e.Line1 > 0 ? e.Line1 : null, e.Column0, true);
        view.FileLocationActivated += (_, e) =>
            _ = openPath(e.Path, e.Line1, e.Column0 + 1, true);
        view.InstallRequested += (_, _) => installLsp();
        view.OpenLspSettingsRequested += (_, _) => openLspSettings();
        view.OpenDocsRequested += (_, url) => openDocs(url);
        controller.OutlineView = view;
        return view;
    }
}
