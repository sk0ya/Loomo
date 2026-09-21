using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// WebView2（ブラウザペイン・プレビュー）へチルトホイールの横スクロールを流す。
/// WPF は WM_MOUSEHWHEEL を routed event にしないため、WebView2 の WPF ラッパーも縦ホイール
/// （<c>OnMouseWheel</c>）しか Chromium へ転送しない。ブラウザペインで横スクロールが効かないのはこれが原因。
/// ここでは公開 API の <see cref="CoreWebView2CompositionController.SendMouseInput"/> に
/// <see cref="CoreWebView2MouseEventKind.HorizontalWheel"/> を送るので、カーソル直下の要素・iframe・
/// PDF ビューアまで Edge と同じ挙動になる（ページへのスクリプト注入は不要）。
/// コントローラを握っているのがラッパーの内部プロパティなので、そこだけリフレクションで辿る。
/// 名前ではなく<b>型</b>で探すので SDK 側のリネームには耐え、辿れなければ false を返して呼び元の次の手段に回す。
/// 座標はラッパーの縦ホイールと同じ「ビュー左上基準・物理ピクセル」で渡す（DIP × DPI スケール）。
/// </summary>
internal static class WebViewHorizontalWheel
{
    private static readonly ReflectedControllerAccessor? ControllerAccess =
        ReflectedControllerAccessor.Resolve(typeof(WebView2CompositionControl), typeof(CoreWebView2Controller));

    /// <summary>SDK 側の内部メンバへ到達できるか（SDK 更新で構造が変わると false ＝テストの見張り対象）。</summary>
    internal static bool CanResolveSdkMembers => ControllerAccess is not null;

    /// <summary>カーソル位置へ横ホイールを送る。送れたら true（呼び元で handled にする）。</summary>
    public static bool TrySend(WebView2CompositionControl view, int delta)
    {
        if (delta == 0 || !view.IsVisible)
            return false;

        // ここは WndProc フックの中なので、例外を漏らすとアプリごと落ちる（App の未処理例外は Handled にしない）。
        // 未実体化なら ResolveController が null を返すので、投げる可能性のある view.CoreWebView2 は触らない。
        try
        {
            if (ResolveController(view) is not { } controller)
                return false;
            var position = Mouse.GetPosition(view);
            var dpi = VisualTreeHelper.GetDpi(view);
            controller.SendMouseInput(
                CoreWebView2MouseEventKind.HorizontalWheel,
                CurrentVirtualKeys(),
                unchecked((uint)delta),   // 負値（左方向）も下位16bitはそのまま＝WM_MOUSEHWHEEL と同じ符号解釈
                new System.Drawing.Point((int)(position.X * dpi.DpiScaleX), (int)(position.Y * dpi.DpiScaleY)));
            return true;
        }
        catch
        {
            return false;   // ブラウザプロセス落ち・破棄直後など。未処理として返すだけで害は無い
        }
    }

    private static CoreWebView2CompositionController? ResolveController(WebView2CompositionControl view)
        => ControllerAccess?.Get(view) as CoreWebView2CompositionController;

    /// <summary>修飾キー・ボタンの状態をそのまま渡す（Ctrl+横ホイール等の解釈はブラウザ側に任せる）。</summary>
    private static CoreWebView2MouseEventVirtualKeys CurrentVirtualKeys()
    {
        var keys = CoreWebView2MouseEventVirtualKeys.None;
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control))
            keys |= CoreWebView2MouseEventVirtualKeys.Control;
        if (modifiers.HasFlag(ModifierKeys.Shift))
            keys |= CoreWebView2MouseEventVirtualKeys.Shift;
        if (Mouse.LeftButton == MouseButtonState.Pressed)
            keys |= CoreWebView2MouseEventVirtualKeys.LeftButton;
        if (Mouse.RightButton == MouseButtonState.Pressed)
            keys |= CoreWebView2MouseEventVirtualKeys.RightButton;
        if (Mouse.MiddleButton == MouseButtonState.Pressed)
            keys |= CoreWebView2MouseEventVirtualKeys.MiddleButton;
        return keys;
    }
}
