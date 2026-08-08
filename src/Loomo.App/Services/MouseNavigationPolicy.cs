namespace sk0ya.Loomo.App.Services;

/// <summary>マウスの戻る/進むボタンを受け取る先。</summary>
public enum MouseNavigationTarget
{
    /// <summary>ブラウザペインの履歴（ポインタがブラウザペインの上にあるとき）。</summary>
    Browser,
    /// <summary>EditorSupport のプレビュー履歴（既定）。</summary>
    EditorSupport,
}

/// <summary>マウスの戻る/進むボタン一回分の宛先と向き。</summary>
public readonly record struct MouseNavigationCommand(MouseNavigationTarget Target, bool Back);

/// <summary>
/// マウスの戻る/進むボタン（XButton1/XButton2）をどのペインの履歴へ配るかの純粋判定。
///
/// <para>この入力はウィンドウ全体の <c>PreviewMouseDown</c> で受けている（WebView2 の上でも取りこぼさない
/// 唯一の場所）。宛先を見ずに EditorSupport へ流していたため、ブラウザペインの上で押した「戻る」が
/// そこで <c>Handled</c> になり、ブラウザの履歴が動かなかった——それがここを切り出した理由。</para>
/// </summary>
public static class MouseNavigationPolicy
{
    /// <summary>押されたボタンとポインタ下のペインから宛先を決める。
    /// 戻る/進む以外のボタンなら <c>null</c>（＝この入力には手を出さない）。</summary>
    public static MouseNavigationCommand? Resolve(MouseButton button, PaneKind? paneUnderPointer)
    {
        var back = button switch
        {
            MouseButton.XButton1 => true,
            MouseButton.XButton2 => false,
            _ => (bool?)null,
        };
        if (back is not { } goBack)
            return null;
        // ブラウザの上ならブラウザの履歴。それ以外（エディタ・ターミナル・ペイン外の余白）は
        // 従来どおり EditorSupport のプレビュー履歴へ。
        var target = paneUnderPointer == PaneKind.Browser
            ? MouseNavigationTarget.Browser
            : MouseNavigationTarget.EditorSupport;
        return new MouseNavigationCommand(target, goBack);
    }
}
