using Editor.Controls;
using Editor.Core.Lsp;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: エディタのガターに出すクイックフィックスの電球（§30.19.2）。列そのものと
/// 「どの行を点けるか」の判定は Editor 側にあり、部屋が決めるのは<b>どのファイルで列を出すか</b>だけ。
/// <para><b>出どころのあるファイルだけ。</b>電球列は有効な間ずっと幅を取る（電球の出入りで本文が
/// 左右に踊らないため）。ということは、クイックフィックスの出どころが無いファイル——<c>.md</c>・
/// <c>.txt</c>・画像——で有効にすると、何も出ない 14px を一生空けておくことになる。<c>.cs</c> は
/// 言語サーバーが無くても部屋が <c>HostCodeActionProvider</c> で答えるので常に対象、それ以外は
/// 対応表に言語サーバーがあるかで決める（テスト列の <see cref="Services.EditorTestGlyphColumns"/> と
/// 同じ「ちらつかせない」考え方）。</para>
/// <para><b>評価の契機は 2 つ。</b>ファイルを読み込んだとき（<see cref="LoadEditorFile"/>＝Loomo 側の
/// 唯一の漏斗）と、タブを活性化したとき（<see cref="OnActiveEditorFileChanged"/>）。同じ値なら
/// Editor 側が no-op にするので、送りすぎる分には害がない。</para></summary>
public partial class ShellWindow {
    /// <summary>1 つのエディタの電球列を、そのファイルに合わせて出す／畳む。</summary>
    private void SyncEditorCodeActionBulb(VimEditorControl control) =>
        control.SetCodeActionBulbEnabled(HasQuickFixSource(control.FilePath));

    /// <summary>このファイルにクイックフィックスの出どころがあるか。</summary>
    private bool HasQuickFixSource(string? filePath) {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var ext = LspExtensions.NormalizeExt(Path.GetExtension(filePath));
        if (ext.Length == 0) return false;
        // C# は言語サーバーが未導入でも部屋が直接答える（StyleCop・コンパイラ診断の修正）。
        if (ext == ".cs") return true;
        return _lspManagement.ResolveServerFor(ext) is not null;
    }
}
