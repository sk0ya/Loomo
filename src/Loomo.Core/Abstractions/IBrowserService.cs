namespace sk0ya.Loomo.Core.Abstractions;

/// <summary>可視 WebView2 ペインのアクティブタブへの操作を抽象化。</summary>
public interface IBrowserService
{
    bool IsAvailable { get; }
    Task<BrowserPageInfo> NavigateAsync(string url, CancellationToken ct);
    /// <summary>ブラウザペインを<b>可視化・フォーカスして</b>アクティブタブを URL へ遷移させ、CoreWebView2 の実体化まで待つ。
    /// （閉じている・未実体のペインでも開いて使えるようにする。フロントデバッグの CDP アタッチ前段で使う。）
    /// ホスト（ShellWindow）が未接続なら通常の <see cref="NavigateAsync"/> にフォールバックする。</summary>
    Task ShowAndNavigateAsync(string url, CancellationToken ct);
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
