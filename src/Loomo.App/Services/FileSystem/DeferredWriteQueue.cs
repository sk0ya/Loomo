using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace sk0ya.Loomo.App.Services;

/// <summary>書き出し専用スレッドで実行される1件ぶんの仕事。</summary>
internal interface IDeferredWork
{
    void Execute();

    /// <summary>自分が <paramref name="older"/> の結果をすべて上書きするか。
    /// 真なら、まだ実行していない古い仕事は捨てられる。</summary>
    bool Covers(IDeferredWork older);
}

/// <summary>追い越しを考えない単発の仕事（SQLite の1文など）。</summary>
internal sealed class DeferredAction(Action work) : IDeferredWork
{
    public void Execute() => work();
    public bool Covers(IDeferredWork older) => false;
}

/// <summary>
/// 「どのファイルへ何を書くか」だけを持つ、スレッドに依らない書き出し計画。
/// <para>
/// 作るのは UI スレッド（＝いまの状態を読めるのはそこだけ）、実行するのは書き出し専用スレッド、という
/// 分け方のための型。計画に載るのは<b>文字列とパスだけ</b>——WPF の要素もエディタのバッファも持ち込まないので、
/// 実行がいつ・どのスレッドで起きても、組み立てた時点の内容がそのまま書かれる。
/// </para>
/// <para>
/// 途中の <see cref="Execute"/> 失敗で残りを捨てない（下書きが1つ書けなくても索引は書く）。
/// 保存は best-effort で、失敗しても人の入力は止めない、が全体の方針。
/// </para>
/// </summary>
internal sealed class FileWritePlan : IDeferredWork
{
    private readonly List<Action> _ops = [];
    private readonly HashSet<string> _targets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>この計画が書き換える対象（ファイル、または刈り取り対象のフォルダ）。
    /// 追い越し判定（<see cref="Covers"/>）だけに使う。</summary>
    public IReadOnlyCollection<string> Targets => _targets;

    public bool IsEmpty => _ops.Count == 0;

    public FileWritePlan EnsureDirectory(string path)
    {
        _ops.Add(() => Directory.CreateDirectory(path));
        return this;
    }

    public FileWritePlan WriteAllText(string path, string text, Encoding? encoding = null)
    {
        _targets.Add(path);
        _ops.Add(() =>
        {
            if (encoding is null) File.WriteAllText(path, text);
            else File.WriteAllText(path, text, encoding);
        });
        return this;
    }

    /// <summary>元が残っていればコピーする（無ければ何もしない）。存在確認も実行時＝背景スレッドで行う。</summary>
    public FileWritePlan CopyIfExists(string source, string destination)
    {
        _targets.Add(destination);
        _ops.Add(() =>
        {
            if (File.Exists(source)) File.Copy(source, destination, overwrite: true);
        });
        return this;
    }

    public FileWritePlan DeleteIfExists(string path)
    {
        _targets.Add(path);
        _ops.Add(() =>
        {
            if (File.Exists(path)) File.Delete(path);
        });
        return this;
    }

    /// <summary><paramref name="keepNames"/> に無いファイルをフォルダから取り除く。
    /// 列挙（＝ディスクを読む）も実行時に行うので、計画を作る側はファイル名の集合を渡すだけでよい。</summary>
    public FileWritePlan PruneDirectory(string directory, string pattern, IReadOnlyCollection<string> keepNames)
    {
        _targets.Add(directory);
        var keep = new HashSet<string>(keepNames, StringComparer.OrdinalIgnoreCase);
        _ops.Add(() =>
        {
            if (!Directory.Exists(directory)) return;
            foreach (var stale in Directory.EnumerateFiles(directory, pattern))
                if (!keep.Contains(Path.GetFileName(stale)))
                    File.Delete(stale);
        });
        return this;
    }

    /// <summary>この計画が <paramref name="older"/> の対象をすべて書き直すか。
    /// 真なら、まだ書いていない古い計画を捨ててよい（結果は同じで、ディスクを叩く回数だけ減る）。</summary>
    public bool Covers(IDeferredWork older)
        => older is FileWritePlan plan && plan._targets.All(_targets.Contains);

    public void Execute()
    {
        foreach (var op in _ops)
        {
            try { op(); }
            catch { /* 1つの失敗で残りを捨てない（保存は best-effort） */ }
        }
    }
}

/// <summary>
/// 書き出し計画を<b>1本のスレッドで順に</b>実行する待ち行列。UI スレッドはここへ積むだけで返る。
/// <para>
/// 1本なのは、積んだ順＝ディスクへ届く順を保つため（索引と本体が入れ替わると、次の起動が古い方を読む）。
/// 追い越された計画——新しい計画が同じ対象を全部書き直すもの——は、実行前なら捨てる。速く打つほど
/// 仕事が減る形で、打鍵中に書き出しが積み上がらない。
/// </para>
/// <para>
/// <see cref="Flush"/> は積まれている分が終わるまで待つ。<b>同期で書いたことを他所から読み直す</b>経路
/// （ワークスペース切替・終了時・削除）は、これを通してから読む／書くこと。
/// </para>
/// </summary>
internal sealed class DeferredWriteQueue : IDisposable
{
    private readonly object _gate = new();
    private readonly List<IDeferredWork> _pending = [];
    private readonly string _threadName;
    private Thread? _worker;
    private bool _busy;
    private bool _disposed;

    public DeferredWriteQueue(string threadName) => _threadName = threadName;

    /// <summary>仕事を積む（呼び出しスレッドは待たない）。</summary>
    public void Enqueue(Action work) => Enqueue(new DeferredAction(work));

    public void Enqueue(IDeferredWork work)
    {
        if (work is FileWritePlan { IsEmpty: true }) return;
        lock (_gate)
        {
            if (_disposed)
            {
                work.Execute();   // 終了後に積まれたものは、その場で書いて落とさない
                return;
            }
            for (var i = _pending.Count - 1; i >= 0; i--)
                if (work.Covers(_pending[i]))
                    _pending.RemoveAt(i);
            _pending.Add(work);
            EnsureWorker();
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>積まれている書き出しが終わるまで待つ。</summary>
    public void Flush()
    {
        lock (_gate)
        {
            while (_pending.Count > 0 || _busy)
                Monitor.Wait(_gate);
        }
    }

    public void Dispose()
    {
        Thread? worker;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            worker = _worker;
            Monitor.PulseAll(_gate);
        }
        worker?.Join(TimeSpan.FromSeconds(5));
    }

    private void EnsureWorker()
    {
        if (_worker is not null) return;
        _worker = new Thread(Loop)
        {
            IsBackground = true,
            Name = _threadName,
            // 人の入力より後回しでよい。CPU を奪い合ったときに負けるのはこちら。
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.Start();
    }

    private void Loop()
    {
        while (true)
        {
            IDeferredWork work;
            lock (_gate)
            {
                while (_pending.Count == 0)
                {
                    _busy = false;
                    Monitor.PulseAll(_gate);   // Flush / Dispose の待ち手を起こす
                    if (_disposed) return;
                    Monitor.Wait(_gate);
                }
                work = _pending[0];
                _pending.RemoveAt(0);
                _busy = true;
            }
            try { work.Execute(); }
            catch { /* 1件の失敗で書き出しスレッドを殺さない */ }
        }
    }
}
