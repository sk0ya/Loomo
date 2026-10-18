using sk0ya.Loomo.Core.Http;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: .http のリクエスト（§24.30）。送受信は <see cref="HttpEditorSupport"/> が持ち、ここは部屋の側の
/// 受け口だけ——応答を素材の流れ（ペグボード・エディタ）へ渡すことと、キャレットから送る入口。
/// </summary>
public partial class ShellWindow {
    private HttpEditorSupport? _httpSupport;

    private void InitializeHttpRequests() {
        _httpSupport = _editorSupports.Resolve("request.http") as HttpEditorSupport;
        if (_httpSupport is null)
            return;
        _httpSupport.PegboardRequested += (_, material) => {
            _vm.Pegboard.AddContent(material.Text, type: "text", title: material.Title);
            ToastService.Info("応答をペグボードへ残しました");
        };
        _httpSupport.OpenInEditorRequested += (_, material) => OpenTextInNewEditorTab(material.Text);
    }

    private static bool IsHttpRequestFile(string? path)
        => Path.GetExtension(path) is { } ext
           && (ext.Equals(".http", StringComparison.OrdinalIgnoreCase) || ext.Equals(".rest", StringComparison.OrdinalIgnoreCase));

    /// <summary>キャレットの行のリクエストを送る。パネルが出ていなければ出す（応答はそこに出る）。</summary>
    private void SendHttpRequestAtCaret() {
        if (_httpSupport is null || _activeEditorTab is not { } tab || !IsHttpRequestFile(tab.PeekFilePath))
            return;
        if (!ReferenceEquals(_editorSupport.Source, tab) || !CanRenderEditorSupport())
            OpenEditorSupport(tab);
        var path = tab.Control.FilePath!;
        _ = SendHttpRequestAsync(path, tab.Control.Text, tab.Control.Caret.Line);
    }

    private async Task SendHttpRequestAsync(string path, string text, int line) {
        var result = await _httpSupport!.SendAtLineAsync(path, text, line);
        if (result is null)
            ToastService.Info("カーソルの位置にリクエストがありません");
    }

    /// <summary>本文を名前の無い新しいタブで開く（言語は本文から決まる・§24.28）。</summary>
    private void OpenTextInNewEditorTab(string text) {
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Editor);
        OnEditorNewTab(this, new RoutedEventArgs());
        _activeEditorTab?.Control.SetText(text);
    }
}
