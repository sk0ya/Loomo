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
        // 拾う2キー以外では何もしない。ここで無条件に UpdateSource すると、PreviewKeyDown は
        // 文字が入る<b>前</b>に来るので「1打ぶん古い語」を毎打鍵で流すことになり、Delay の意味が消える。
        if (e.Key is not (Key.Escape or Key.Down)) return;

        // 待たせている語を先に確定させる。↓ の行き先（絞り込み後の一覧）は、押した瞬間の
        // 見た目と一致していなければならない。
        PushFilterTermNow(box);

        switch (ResolveBranchFilterKey(e.Key, box.Text, BranchList.Items.Count > 0))
        {
            case BranchFilterKeyAction.Clear:
                box.Clear();
                PushFilterTermNow(box);
                e.Handled = true;
                break;
            case BranchFilterKeyAction.MoveToList:
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
