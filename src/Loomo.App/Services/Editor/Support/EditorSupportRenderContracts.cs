using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Services;

/// <summary>1回分の描画に必要な入力。UIから読む値をawaitの前にスナップショットする。</summary>
internal sealed record EditorSupportRenderRequest(
    EditorTab Source,
    string? FilePath,
    string Text,
    int CaretLine,
    int CaretColumn,
    ILspDocument? Lsp,
    string PreviewTheme);

/// <summary>描画エンジンが必要とするWPF・WebView2・タイマー操作。</summary>
internal interface IEditorSupportRenderHost
{
    Task<bool> EnsureWebViewAsync();
    Task<string?> PreparePageAsync(string html, CancellationToken ct);
    string? ReadyPageKey { get; }
    void ClearFullPageRequest();
    LspNoticeModel.Notice? DiagnoseLsp(string filePath);
    void ScheduleLspReadyRetry();
    void StopLspReadyRetry();
}
