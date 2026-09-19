using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace sk0ya.Loomo.App.Views;

/// <summary>ブランチ絞り込み欄でキーに割り当てる操作。</summary>
public enum BranchFilterKeyAction
{
    None,

    /// <summary>絞り込み語を消す。</summary>
    Clear,

    /// <summary>一覧へ降りる（打ってから結果を選ぶ、を1ストロークで繋ぐ）。</summary>
    MoveToList,
}

/// <summary>
/// Git ペインのキーボード操作。マウス前提の面にしないための最小限——部屋の他の面
/// （FolderTree の j/k・"/"）と手癖が違うと、ステージに上げて全面で読んでいる最中に手が止まる。
/// </summary>
public partial class GitSessionView
{
    /// <summary>
    /// 絞り込み欄のキー割り当て（純ロジック）。判断材料は<b>入力欄の今の文字</b>であって VM の値ではない
    /// ——バインディングには <c>Delay</c> があり、打った直後の 120ms は VM がまだ空なので、
    /// VM を見て決めると「打ってすぐ Esc」（＝打ち間違いを取り消す、いちばん有りそうな場面）で
    /// 何も消えずにキーだけ素通りする。
    /// </summary>
    public static BranchFilterKeyAction ResolveBranchFilterKey(Key key, string text, bool hasRows) => key switch
    {
        Key.Escape when text.Length > 0 => BranchFilterKeyAction.Clear,
        // 降りる先が無いときは握り潰さない（効かないキーを飲み込むと、外側の割り当ても効かなくなる）
        Key.Down when hasRows => BranchFilterKeyAction.MoveToList,
        _ => BranchFilterKeyAction.None,
    };

    private void OnBranchFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;

        switch (e.Key)
        {
            case Key.Escape:
                // 消す前に語を流さない。流すと、捨てるつもりの語で一瞬だけ絞り込みが効いて
                // 一覧が作り直され、選択も開きかけの操作メニューも落ちる。消してから1回だけ流す
                // （この Delay 待ちを踏まないと、打った直後の Esc が素通りする）。
                if (ResolveBranchFilterKey(e.Key, box.Text, hasRows: false)
                    is not BranchFilterKeyAction.Clear) return;
                box.Clear();
                PushFilterTermNow(box);
                e.Handled = true;
                break;

            case Key.Down:
                // 行き先は「絞り込んだ後の一覧」なので、待たせている語を先に確定させる。
                PushFilterTermNow(box);
                if (ResolveBranchFilterKey(e.Key, box.Text, BranchList.Items.Count > 0)
                    is not BranchFilterKeyAction.MoveToList) return;
                MoveFocusToBranchList();
                e.Handled = true;
                break;
        }
    }

    /// <summary>TextBox の <c>Delay</c> を待たずに語を VM へ流す（待ち中の更新はこれで置き換わる）。</summary>
    private static void PushFilterTermNow(TextBox box) =>
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

    /// <summary>絞り込み欄から一覧の先頭行へ降りる。</summary>
    private void MoveFocusToBranchList()
    {
        // ItemsSource を差し替えた直後は行コンテナがまだ無い（生成はレイアウト後）。
        BranchList.UpdateLayout();
        if (BranchList.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem first)
            first.Focus();
        else
            BranchList.Focus();
    }
}
