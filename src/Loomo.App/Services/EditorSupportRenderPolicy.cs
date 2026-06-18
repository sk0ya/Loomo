namespace sk0ya.Loomo.App.Services;

/// <summary>
/// EditorSupport ペインの「中身（Markdown プレビュー等）を描くか」の純粋判定。
/// UI（<c>ShellWindow.UpdateEditorSupportAsync</c>）から切り出してテスト可能にしてある。
/// 自動表示はせず（明示操作でのみ開閉）、ペインが実際に表示されているときだけ描く。
/// ソロ（舞台）モードと、タイル表示の可視で表現が分かれるため、その境界をここで一元化する。
/// </summary>
public static class EditorSupportRenderPolicy
{
    /// <summary>
    /// EditorSupport の内容を描画すべきか。ペインが実際に表示されているときだけ true。
    /// <list type="bullet">
    /// <item>ソロモードで EditorSupport が舞台に立っている（<paramref name="onStage"/>）。</item>
    /// <item>または、タイル表示でレイアウト上に可視（<paramref name="paneVisibleInLayout"/>）。</item>
    /// </list>
    /// どちらでもない（閉じている）なら描かない＝自動では開かない。
    /// </summary>
    public static bool ShouldRender(bool onStage, bool paneVisibleInLayout)
        => onStage || paneVisibleInLayout;
}
