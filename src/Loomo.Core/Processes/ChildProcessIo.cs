using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Core.Processes;

/// <summary>
/// 子プロセスの標準出力／標準エラーを読むための唯一の入口。**スレッドプールを使わない**のが要点。
/// <para><b>なぜ要るのか</b>——<see cref="Process.StandardOutput"/> のパイプは同期ハンドルで開かれるので、
/// <c>ReadToEndAsync</c>／<c>ReadLineAsync</c>／<c>BeginOutputReadLine</c> はどれも async-over-sync に落ちる
/// （<c>Kernel32.ReadFile</c> で待つ）。つまり「非同期に読んでいるつもり」で、実際には<b>子プロセスが
/// 終わるまでプールのワーカーを1本占有する</b>。プールの下限はコア数で、自動増員は飢餓検知で毎秒1〜2本しか
/// 進まない。だから子プロセスを数本並べただけでプールは詰まり、そこへ積まれた仕事は数秒待たされる。</para>
/// <para>実際に踏んだ姿（2026-09-24）——起動直後に走る C# プロジェクト評価の <c>dotnet msbuild</c> 4並列
/// （stdout+stderr で8本）＋ git で6コア機のプールが丸ごと埋まり、ターミナルの ConPTY を起こす
/// <c>Task.Run</c> がその行列の後ろで待たされた。窓は2秒で出ているのに<b>プロンプトだけ5〜9秒遅れる</b>。
/// 人間のシェルが背景のインデックス作業に負けていたわけで、これは調整ではなく構造の誤り。</para>
/// <para>だから読み取りは<b>専用スレッド</b>で行う。プールの仕事ではないものをプールに積まない、という
/// 一点だけの決まりで、補完ワーカー（<c>FimCompletionClient</c>、別プロセス＋専用スレッド＋
/// <see cref="ProcessPriorityClass.BelowNormal"/>）が先に守っていた作法を子プロセス全体へ広げたもの。
/// スレッドは数本増えるが、どれも I/O 待ちで CPU を食わない——プールを塞ぐのとは意味が違う。</para>
/// <para><b>打ち切りは「殺して閉じる」</b>——ブロッキング読みは途中で降りられないので、
/// <see cref="CancellationToken"/> を読み取り側に渡す口はわざと無い。呼び出し側がプロセスを
/// kill すればパイプが閉じ、読みは EOF で自然に終わる（各呼び出し側の kill 経路がそのまま効く）。</para>
/// </summary>
public static class ChildProcessIo
{
    /// <summary>ストリームを最後まで読み切る。返る Task の継続は読み取りスレッドでは走らない。
    /// <para>kill でパイプが閉じるのは EOF なので、そこまでのぶんが普通に返る（打ち切りの作法が
    /// 「殺して閉じる」である以上それは異常ではない）。<b>途中で壊れた読みは失敗のまま返す</b>——
    /// 短い結果を成功として返すと、呼び出し側は終了コードだけを見て「git の出力が全部来た」と
    /// 誤解する（例：<c>git status</c> の取りこぼしが、そのままツリーの差分表示の欠落になる）。
    /// 見捨てられた読み（プロセスごと破棄した後）は静かに終える。</para></summary>
    public static Task<string> ReadToEndAsync(StreamReader reader, string threadName)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return RunOffPoolAsync(threadName, () =>
        {
            var text = new StringBuilder();
            var buffer = new char[4096];
            try
            {
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    text.Append(buffer, 0, read);
            }
            catch (ObjectDisposedException) { /* 見捨てられた読み（プロセスを破棄した） */ }
            return text.ToString();
        });
    }

    /// <summary>行が来るたび <paramref name="onLine"/> を呼ぶ。<c>BeginOutputReadLine</c> の置き換え。
    /// <paramref name="onLine"/> は読み取りスレッドで走るので、共有状態は呼び出し側で守ること
    /// （プールで発火していた頃と同じ約束）。こちらは<b>届いた行がすべて</b>で、途中で切れても
    /// 呼び出し側にできることは無いので、終端・パイプ切断のどちらでも静かに終わる
    /// （<c>BeginOutputReadLine</c> のときと同じ振る舞い）。</summary>
    public static Task PumpLinesAsync(StreamReader reader, Action<string> onLine, string threadName)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(onLine);
        return RunOffPoolAsync<object?>(threadName, () =>
        {
            try
            {
                while (reader.ReadLine() is { } line)
                    onLine(line);
            }
            catch (IOException) { /* パイプが閉じた */ }
            catch (ObjectDisposedException) { /* プロセスが破棄された */ }
            return null;
        });
    }

    /// <summary>子プロセスを待つ同期の仕事を、まるごと専用スレッドで走らせる汎用の口。
    /// 読み取りループを行数で打ち切りたいとき、あるいは「子プロセスを何本か起こして待つ」
    /// 同期メソッド（git の連続実行など）を呼ぶときに使う。
    /// <para>ここへ渡してよいのは<b>子プロセスの終了やパイプを待つだけ</b>の仕事に限る。
    /// CPU を回す計算は <see cref="Task.Run(Action)"/>＝プールの仕事のままにしておくこと——
    /// プールが悪いのではなく、<b>待つだけの仕事でプールの椅子を占めるのが悪い</b>。</para></summary>
    public static Task<T> RunOffPoolAsync<T>(string threadName, Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(work()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })
        {
            IsBackground = true,   // 待ち手が残ってもアプリの終了を止めない
            Name = threadName,
        };
        thread.Start();
        return completion.Task;
    }

    /// <summary>背景の仕事（索引・評価）として起動した子プロセスを、人間の操作より下の優先度に落とす。
    /// 部屋の主役は人間なので、背景仕事が CPU で人間の道具と対等に競るのは構造の誤り——
    /// 補完ワーカーと同じ考え方を、明示的に背景と分かっている子プロセスにも適用する。
    /// 起動直後に終了していることがあるので失敗は黙って無視する。</summary>
    public static void TrySetBackgroundPriority(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (InvalidOperationException) { /* 既に終了 */ }
        catch (System.ComponentModel.Win32Exception) { /* 権限・競合 */ }
    }
}
