namespace sk0ya.Loomo.Core.Abstractions;

/// <summary>可視 WebView2 ペインのアクティブタブへの操作を抽象化。</summary>
public interface IBrowserService
{
    bool IsAvailable { get; }
    Task<BrowserPageInfo> NavigateAsync(string url, CancellationToken ct);
    Task<BrowserPageInfo> GetPageInfoAsync(CancellationToken ct);
    Task<IReadOnlyList<BrowserClickable>> ListClickablesAsync(CancellationToken ct);
    Task<string> GetVisibleTextAsync(CancellationToken ct);
    Task<string> EvaluateScriptAsync(string script, CancellationToken ct);
    Task ClickAsync(string selector, CancellationToken ct);
    Task TypeAsync(string selector, string text, CancellationToken ct);
    Task<byte[]> CaptureScreenshotAsync(CancellationToken ct);
}

/// <summary>ブラウザの現在ページ情報。</summary>
public sealed record BrowserPageInfo(string Url, string Title);
/// <summary>ページ内のクリックまたは入力可能な要素。</summary>
public sealed record BrowserClickable(string Tag, string Text, string Selector);
