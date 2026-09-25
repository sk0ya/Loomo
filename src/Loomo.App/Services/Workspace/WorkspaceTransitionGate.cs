namespace sk0ya.Loomo.App.Services;

/// <summary>
/// ワークスペース切替の<b>途中</b>かどうかを知らせ、途中に届いた要求を切替が終わるまで待たせる。
/// <para>
/// 切替は <c>await</c> をまたぐ（ツリー投入・端末・エディタ・ブラウザの順に、間で描画を挟む）。その隙間では
/// 前のワークスペースのタブは外したが、エディタのタブ集合（<c>_editorTabs</c>）とその「いまのタブ」の記録は
/// <b>まだ前のワークスペースのもの</b>を指している。そこへファイルを開く要求が割り込むと、
/// 開いたタブが前のワークスペースへ紛れ込み、前のワークスペースのアクティブタブまで書き換わる——
/// 戻ってきたとき EditorSupport が別のワークスペースのファイル（PDF 等）を出し続けるのはこれだった。
/// 割り込んでくる経路（ツリーの選択プレビュー・パレット・リンク・軌跡…）は増え続けるので、
/// 経路ごとに塞ぐのではなく、タブ集合を触る入口で<b>切替が終わるのを待つ</b>。
/// </para>
/// <para>
/// 切替は <see cref="WorkspaceSwitchRequestCoordinator"/> で直列化されているが、起動時の復元は
/// その外から入るので、重なっても数えられるよう入れ子で持つ。すべて UI ディスパッチャ上で使う前提。
/// </para>
/// </summary>
internal sealed class WorkspaceTransitionGate
{
    private TaskCompletionSource? _settled;
    private int _depth;
    /// <summary><see cref="Begin"/> のたびに進む。「開き始めてから切替が挟まったか」を見るための印。</summary>
    private int _epoch;

    /// <summary>いま切替の途中か。</summary>
    public bool IsSwitching => _depth > 0;

    /// <summary>
    /// いまの世代。<c>await</c> をまたぐ処理は始める前にこれを取っておき、戻ってきたら
    /// <see cref="HasSwitchedSince"/> で確かめる——入口で <see cref="IsSwitching"/> を見るだけでは、
    /// <b>切替より前に始まって途中で待っていた</b>処理が、切替の途中や後に前のワークスペースのつもりで続きを走らせる。
    /// </summary>
    public int Epoch => _epoch;

    /// <summary><paramref name="epoch"/> を取ってから切替が始まった（または途中である）か。</summary>
    public bool HasSwitchedSince(int epoch) => IsSwitching || _epoch != epoch;

    /// <summary>切替に入る。戻り値を破棄したときに抜ける（例外で抜けても必ず開く）。</summary>
    public IDisposable Begin()
    {
        _epoch++;
        if (_depth++ == 0)
            _settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return new Scope(this);
    }

    /// <summary>
    /// 切替が終わるまで待つ。途中でなければ完了済みの Task を返すので、平常時の呼び出しは待たない。
    /// 待っている間に次の切替が始まったら、それも待つ（開いたタブを必ず<b>最後に落ち着いた</b>ワークスペースへ入れる）。
    /// </summary>
    public async Task WhenSettledAsync()
    {
        while (_settled is { } settled)
            await settled.Task;
    }

    private void End()
    {
        if (_depth == 0 || --_depth > 0)
            return;
        var settled = _settled;
        _settled = null;
        settled?.TrySetResult();
    }

    private sealed class Scope(WorkspaceTransitionGate gate) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            gate.End();
        }
    }
}
