using Editor.Controls;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// Editor のタブの文書に起きたことのうち、<c>BufferChanged</c> では届かないものを知らせる。同じ文書を
/// 別のエディタに映している側（切り離し窓の複製・Diff の右側）は本文の変化を <c>BufferChanged</c> で
/// 追うが、次の3つはそれでは分からない：
/// <list type="bullet">
/// <item><see cref="Loaded"/>：ディスクから本文を入れた（開いた・ブランチ切替や一括置換での読み直し）。
/// <c>LoadFile</c> は <c>BufferChanged</c> を出さない。知らないまま映し側で打つと、古い本文で上書きする。</item>
/// <item><see cref="Saved"/>：保存した。映し側は本文が同じでも「未保存」のまま残る。</item>
/// <item><see cref="Closed"/>：タブを閉じた（コントロールは破棄済み）。映し側は追従をやめる。</item>
/// </list>
/// どれも UI スレッドで発火する。
/// </summary>
internal sealed class EditorDocumentEvents
{
    public event Action<VimEditorControl>? Loaded;
    public event Action<VimEditorControl>? Saved;
    public event Action<VimEditorControl>? Closed;

    public void RaiseLoaded(VimEditorControl control) => Loaded?.Invoke(control);
    public void RaiseSaved(VimEditorControl control) => Saved?.Invoke(control);
    public void RaiseClosed(VimEditorControl control) => Closed?.Invoke(control);
}
