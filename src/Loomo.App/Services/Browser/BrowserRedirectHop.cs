using Microsoft.Web.WebView2.Core;

namespace sk0ya.Loomo.App.Services;

/// <summary>ページ自身が始めたリダイレクト（<c>location.replace</c>・meta refresh・Bing/Google の
/// クリック計測ページ・ログインの中継など）を見分ける。<b>サーバーのリダイレクトは 1 回の遷移の中で
/// 済む</b>ので完了は 1 度しか来ないが、こちらは中継ページの読み込みが一度「完了」してから次の遷移が
/// 始まるため、素直に記録すると軌跡に中継ページの点が混ざる。その点を次の行き先で置き換えるための判定。</summary>
internal static class BrowserRedirectHop
{
    /// <summary>中継ページとみなす滞在の上限。これより長く居たページは人が見たページとして残す。</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    /// <summary>人の操作でもホストの API（アドレス欄・戻る/進む・再読み込み）でもない、
    /// 直前の読み込みから間もない新規ドキュメントへの遷移か。
    /// WebView2 は API 経由の遷移を <c>IsUserInitiated</c> = true として扱う。</summary>
    internal static bool IsHop(
        bool isUserInitiated, CoreWebView2NavigationKind kind, DateTime? lastCompletedUtc, DateTime nowUtc)
        => !isUserInitiated
           && kind == CoreWebView2NavigationKind.NewDocument
           && lastCompletedUtc is { } completed
           && nowUtc - completed <= Window;
}
