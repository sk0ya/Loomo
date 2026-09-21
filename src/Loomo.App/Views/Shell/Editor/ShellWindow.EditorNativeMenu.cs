namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: エディタ右クリックメニューの<b>ネイティブ項目の取捨</b>。
///
/// <para>ライブラリ（sk0ya.Editor.Controls）は自分のメニューを組んでからホストの
/// <c>ContextMenuBuilding</c> を呼ぶ。ホストに渡ってくるのは組み上がった
/// <see cref="ContextMenu"/> そのものなので、Loomo は「足す」だけでなく
/// <b>外す・差し替える</b>こともここで行う。目印は
/// <see cref="EditorMenuLabels"/> の見出し——その表で名付けた項目だけを触る。</para>
///
/// <para><b>外す 3 本</b>（元に戻す／やり直す／すべて選択）は、Vim の <c>u</c> /
/// <c>Ctrl+R</c> / <c>ggVG</c> と Ctrl+Z / Ctrl+Y / Ctrl+A が常に効くうえ、
/// 「右クリックした<b>その位置</b>に対して何をするか」ではない。右クリックは位置の操作を選ぶ場所で、
/// 履歴と全選択はそこに要らない。</para>
///
/// <para><b>差し替える 2 本</b>は、どちらも押しても目的を果たせなかった項目：
/// 「この位置で使える修正」はキャンバスに描かれる候補ポップアップを開くだけで、
/// <b>マウスでは選べない</b>（j/k と Enter でしか適用できない）——右クリックから入った操作なのに
/// そこから先がキーボード専用だった。「この位置の説明を表示」は hover の本文を
/// ステータスバーへ<b>1 行だけ</b>出すため、Markdown で返るサーバーでは
/// 実測で <c>```csharp</c> というコードフェンスだけが表示されていた。
/// どちらも Loomo 側の項目（クリックで選べる Quick Fix サブメニュー／本文を出すポップアップ）へ
/// 同じ位置で置き換える。</para></summary>
public partial class ShellWindow
{
    /// <summary>ネイティブ項目を Loomo の方針へ整える。<paramref name="anchor"/> は右クリック位置
    /// （＝キャレット位置）で、説明ポップアップの表示位置に使う。</summary>
    private void AdjustNativeEditorMenuItems(
        ContextMenu menu, VimEditorControl? control, Point anchor)
        => EditorNativeMenuCoordinator.Adjust(
            menu,
            control is not null,
            () => BuildQuickFixMenuItem(control!),
            () => BuildHoverInfoMenuItem(control!, anchor));
}
