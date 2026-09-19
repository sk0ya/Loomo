using System;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using sk0ya.Loomo.Services;

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

/// <summary>コミット一覧でキーに割り当てる操作。</summary>
public enum GitLogKeyAction
{
    None,
    MoveDown,
    MoveUp,
    MoveTop,
    MoveBottom,

    /// <summary>選択中のコミットの差分を出す（ダブルクリックと同じ）。</summary>
    OpenDiff,

    /// <summary>ヘッダーの絞り込み欄へ移る。</summary>
    FocusFilter,

    /// <summary>絞り込みをすべて解除する。</summary>
    ClearFilter,
}

/// <summary>キー1打の結果。<paramref name="PendingG"/> は "gg" の1つ目を待っている状態。</summary>
public readonly record struct GitLogKeyResult(GitLogKeyAction Action, bool PendingG);

/// <summary>
/// Git ペインのキーボード操作。マウス前提の面にしないための最小限——部屋の他の面
/// （FolderTree の j/k・gg/G・"/"）と手癖が違うと、ステージに上げて全面で読んでいる最中に手が止まる。
/// </summary>
public partial class GitSessionView
{
    // ===== ブランチ絞り込み欄 =====

    /// <summary>
    /// 絞り込み欄のキー割り当て（純ロジック）。判断材料は<b>入力欄の今の文字</b>であって VM の値ではない
    /// ——バインディングには <c>Delay</c> があり、打った直後の 120ms は VM がまだ空なので、
    /// VM を見て決めると「打ってすぐ Esc」（＝打ち間違いを取り消す、いちばん有りそうな場面）で
    /// 何も消えずにキーだけ素通りする。
    /// </summary>
    public static BranchFilterKeyAction ResolveBranchFilterKey(Key key, string text, bool hasRows) => key switch
    {
        Key.Escape when text.Length > 0 => BranchFilterKeyAction.Clear,
        // 空の状態での Esc は出口にする（キーボードで入ったのに出られない入力欄を作らない）
        Key.Escape => BranchFilterKeyAction.MoveToList,
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
                // Esc は必ず握る。TextBox の既定の Esc は Undo で、握らないと
                // ApplicationCommands.Undo がそのまま昇っていく（エディタの取り消しまで巻き込む）。
                e.Handled = true;
                // 消す前に語を流さない。流すと、捨てるつもりの語で一瞬だけ絞り込みが効いて
                // 一覧が作り直され、選択も開きかけの操作メニューも落ちる。消してから1回だけ流す
                // （この Delay 待ちを踏まないと、打った直後の Esc が素通りする）。
                if (ResolveBranchFilterKey(e.Key, box.Text, hasRows: false)
                    is BranchFilterKeyAction.Clear)
                {
                    box.Clear();
                    PushFilterTermNow(box);
                }
                else
                {
                    MoveFocusToBranchList();
                }
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

    // ===== ブランチ一覧 =====

    /// <summary>
    /// 一覧の上では "/" で絞り込み欄へ戻り、Enter で（ダブルクリックと同じく）そのブランチの
    /// コミットを出す。上下移動と開閉は TreeView が元から持っているので足さない。
    /// </summary>
    private async void OnBranchListKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers is not ModifierKeys.None) return;

        if (e.Key is Key.OemQuestion or Key.Divide)
        {
            e.Handled = true;
            BranchFilterBox.Focus();
            BranchFilterBox.SelectAll();
            return;
        }

        if (e.Key is not Key.Enter || Vm is not { } vm || SelectedTreeBranch is not { } branch) return;
        e.Handled = true;
        // ダブルクリックとまったく同じ経路（世代で番をして、追い越された失敗は黙らせる）。
        await ShowBranchLogGuardedAsync(vm, branch);
    }

    // ===== コミット一覧 =====

    /// <summary>"gg" の1打目を待っているか。</summary>
    private bool _pendingLogG;

    /// <summary>
    /// コミット一覧のキー割り当て（純ロジック）。修飾キー付きは<b>すべて素通りさせる</b>
    /// ——Ctrl+C（コピー）や Alt のメニュー起動まで奪うと、一覧の上だけ部屋の作法が変わる。
    /// "g" は1打目を飲み込んで2打目を待つ（"gg" で先頭）。他のキーが来れば待ちは解ける。
    /// </summary>
    public static GitLogKeyResult ResolveLogKey(Key key, ModifierKeys modifiers, bool pendingG, bool hasFilters)
    {
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
            return new(GitLogKeyAction.None, false);

        var shift = (modifiers & ModifierKeys.Shift) != 0;
        return key switch
        {
            Key.G when shift => new(GitLogKeyAction.MoveBottom, false),
            Key.G when pendingG => new(GitLogKeyAction.MoveTop, false),
            Key.G => new(GitLogKeyAction.None, PendingG: true),
            Key.J when !shift => new(GitLogKeyAction.MoveDown, false),
            Key.K when !shift => new(GitLogKeyAction.MoveUp, false),
            Key.Enter => new(GitLogKeyAction.OpenDiff, false),
            Key.OemQuestion or Key.Divide when !shift => new(GitLogKeyAction.FocusFilter, false),
            // 絞り込んでいないときの Esc は<b>何もしない</b>。解除は git への引き直しで、読み込み済みの
            // ページを全部捨てて先頭へ戻す＝深く手繰った場所と選択を、消すものが無いのに失う。
            // ヘッダーの「✕ 解除」も同じ条件でしか出ない。
            Key.Escape when hasFilters => new(GitLogKeyAction.ClearFilter, false),
            _ => new(GitLogKeyAction.None, false),
        };
    }

    /// <summary>ヘッダー（ShellWindow 側）のコミット絞り込み欄へフォーカスを移してほしい。
    /// 絞り込み欄はペインの外に住んでいるので、ここからは触れない。</summary>
    public event EventHandler? LogFilterFocusRequested;

    /// <summary>
    /// ペインへフォーカスが来たときの着地点。一覧に居ればそのままキーが効く。
    /// <b>選択は動かさない</b>——選択は TwoWay で VM へ流れて <c>git show</c> まで走るので、
    /// 「フォーカスを移しただけ」で詳細が差し替わるのは副作用が大きすぎる（選択が無い状態でも
    /// j は先頭のコミットから始まる）。
    /// </summary>
    public void FocusCommitList()
    {
        _pendingLogG = false;
        if (LogList.Items.Count == 0) { Focus(); return; }
        LogList.Focus();
        FocusSelectedLogRow();
    }

    /// <summary>一覧から離れたら "gg" の待ちを捨てる（戻ってきた1打目が先頭へ飛ぶのを防ぐ）。</summary>
    private void OnLogListLostFocus(object sender, KeyboardFocusChangedEventArgs e) => _pendingLogG = false;

    private void OnLogListKeyDown(object sender, KeyEventArgs e)
    {
        var result = ResolveLogKey(e.Key, Keyboard.Modifiers, _pendingLogG,
            Vm?.History.HasActiveFilters ?? false);
        _pendingLogG = result.PendingG;

        if (result.Action is GitLogKeyAction.None)
        {
            // "gg" の1打目は飲み込む（ListView の頭文字ジャンプに食われると2打目が来ない）。
            if (result.PendingG) e.Handled = true;
            return;
        }

        e.Handled = true;
        switch (result.Action)
        {
            case GitLogKeyAction.MoveDown:
                SelectLogRowAt(FindCommitIndex(SelectedLogIndex + 1, forward: true));
                break;
            case GitLogKeyAction.MoveUp:
                SelectLogRowAt(FindCommitIndex(SelectedLogIndex - 1, forward: false));
                break;
            case GitLogKeyAction.MoveTop:
                SelectLogRowAt(FindCommitIndex(0, forward: true));
                break;
            case GitLogKeyAction.MoveBottom:
                SelectLogRowAt(FindCommitIndex(LogList.Items.Count - 1, forward: false));
                break;
            case GitLogKeyAction.OpenDiff:
                OnCommitShowDiff(sender, e);
                break;
            case GitLogKeyAction.FocusFilter:
                LogFilterFocusRequested?.Invoke(this, EventArgs.Empty);
                break;
            case GitLogKeyAction.ClearFilter:
                Vm?.History.ClearLogFiltersCommand.Execute(null);
                break;
        }
    }

    /// <summary>
    /// いま居る行。<b>キーボードフォーカスのある行を優先する</b>——SelectionMode は Extended で、
    /// 範囲選択（コミット間の比較のために Shift+クリックで広げる）のあとの
    /// <c>SelectedItem</c> は選択の<b>先頭</b>を指すので、それを起点にすると j が範囲の長さぶん
    /// 逆戻りする。
    /// </summary>
    private int SelectedLogIndex
    {
        get
        {
            if (Keyboard.FocusedElement is ListViewItem focused
                && ItemsControl.ItemsControlFromItemContainer(focused) == LogList)
                return LogList.ItemContainerGenerator.IndexFromContainer(focused);
            return LogList.SelectedItem is { } item ? LogList.Items.IndexOf(item) : -1;
        }
    }

    /// <summary>
    /// <paramref name="from"/> から指定方向へ、最初の<b>コミット行</b>の位置を返す（無ければ -1）。
    /// 枝の継続行（"| |" だけの行）を飛ばすのは、そこに止まってもコミットが選ばれず
    /// 詳細が空になるだけだから——押した回数と進む行数が合わない見え方になる。
    /// </summary>
    private int FindCommitIndex(int from, bool forward)
    {
        var step = forward ? 1 : -1;
        for (var i = from; i >= 0 && i < LogList.Items.Count; i += step)
            if (LogList.Items[i] is GitLogRow { IsCommit: true })
                return i;
        return -1;
    }

    private void SelectLogRowAt(int index)
    {
        if (index < 0 || index >= LogList.Items.Count) return;
        LogList.SelectedIndex = index;
        LogList.ScrollIntoView(LogList.Items[index]);
        FocusSelectedLogRow();
    }

    /// <summary>選択行のコンテナへフォーカスを移す（次のキーが同じ一覧へ届くように）。</summary>
    private void FocusSelectedLogRow()
    {
        if (LogList.SelectedItem is not { } item) return;
        LogList.UpdateLayout();
        if (LogList.ItemContainerGenerator.ContainerFromItem(item) is ListViewItem container)
            container.Focus();
    }
}
