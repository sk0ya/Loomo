using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// スクロール等のカクつき原因を「UI スレッドの占有」か「描画（合成/DWM）側のヒッチ」かへ切り分ける
/// 開発時専用プロファイラ。環境変数 <c>LOOMO_JANK_PROFILE=1</c> のときだけ動く（既定は完全に無効）。
///
/// <para>2 系統を同時に測る：</para>
/// <list type="bullet">
/// <item><b>UI キュー遅延</b>：バックグラウンドスレッドが Dispatcher へ <c>Normal</c> 優先度の ping を投げ、
/// 実行されるまでの往復時間を計る。これが跳ねる＝UI スレッドがハンドラ／レイアウトで詰まっている
/// （＝「UI スレッドを奪う処理」がある）。</item>
/// <item><b>フレーム間隔</b>：<see cref="CompositionTarget.Rendering"/> の間隔を計る。UI キュー遅延は
/// 平穏なのにフレーム間隔だけ跳ねる＝レンダースレッド／合成側のヒッチ（アプリコードの外）。</item>
/// </list>
///
/// <para>しきい値超えのイベントだけを <c>%APPDATA%/Loomo/jank.log</c> へ即時追記する。スクロールして
/// カクついた瞬間の行を読めば、UI 起因か描画起因かが一目で分かる。</para>
/// </summary>
internal static class UiJankProfiler
{
    private static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("LOOMO_JANK_PROFILE"), "1", StringComparison.Ordinal);

    /// <summary>これ以上遅れた UI ping / フレーム間隔だけを記録する（平穏時のノイズを出さない）。</summary>
    private const long StallThresholdMs = 24;

    /// <summary>UI スレッドへ ping を投げる間隔。60fps（≒16ms）より細かく刻んで stall を捉える。</summary>
    private const int PingIntervalMs = 4;

    /// <summary>
    /// ping が返るのをここまで待つ。
    ///
    /// <para>ここは短くしてはいけない。以前は 2 秒で打ち切って次周期へ回していたので、
    /// <b>利用者が「固まる」と呼ぶ 2 秒超の停止だけが UIWAIT から漏れていた</b>
    /// （4.7 秒の停止が DISPATCH 行にしか残っていなかったのはこのため）。</para>
    /// </summary>
    private const int PingWaitCapMs = 60_000;

    private static readonly object Lock = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "jank.log");

    private static Dispatcher? _dispatcher;
    private static Thread? _pingThread;
    private static volatile bool _running;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static long _lastFrameMs = -1;

    /// <summary>プロファイラを開始する（UI スレッドから一度だけ呼ぶ）。無効時は何もしない。</summary>
    public static void Start(Dispatcher dispatcher)
    {
        if (!Enabled || _running)
            return;
        _running = true;
        _dispatcher = dispatcher;

        TryTruncate();

        // Dispatcher 経由の長時間オペレーションを、その呼び出し先メソッド名付きで記録する
        // （BeginInvoke で回っている再入ループ＝周期ジャンクの犯人を名指しするため）。
        dispatcher.Hooks.OperationStarted += OnOperationStarted;
        dispatcher.Hooks.OperationCompleted += OnOperationCompleted;

        CompositionTarget.Rendering += OnRendering;

        _pingThread = new Thread(PingLoop)
        {
            IsBackground = true,
            Name = "UiJankPing",
            Priority = ThreadPriority.AboveNormal,
        };
        _pingThread.Start();
    }

    public static void Stop()
    {
        _running = false;
        if (_dispatcher is not null)
        {
            CompositionTarget.Rendering -= OnRendering;
            _dispatcher.Hooks.OperationStarted -= OnOperationStarted;
            _dispatcher.Hooks.OperationCompleted -= OnOperationCompleted;
        }
    }

    private static readonly ConcurrentDictionary<DispatcherOperation, long> OpStart = new();

    /// <summary>Dispatcher オペレーション開始時刻を控える。</summary>
    private static void OnOperationStarted(object? sender, DispatcherHookEventArgs e)
        => OpStart[e.Operation] = Clock.ElapsedMilliseconds;

    /// <summary>完了時に所要を計り、しきい値超えなら呼び出し先メソッド名付きで記録する。</summary>
    private static void OnOperationCompleted(object? sender, DispatcherHookEventArgs e)
    {
        if (!OpStart.TryRemove(e.Operation, out var start))
            return;
        var dur = Clock.ElapsedMilliseconds - start;
        if (dur >= StallThresholdMs)
            Append($"{Clock.ElapsedMilliseconds,8} ms  DISPATCH dur={dur,5} ms  pri={e.Operation.Priority,-11}  {DescribeOperation(e.Operation)}");
    }

    /// <summary>DispatcherOperation の呼び出し先デリゲート（＝実行される Loomo のメソッド）を反射で取り出す。
    /// async 継続は共通ラッパ（SynchronizationContextAwaitTaskContinuation）で包まれ本体が隠れるので、
    /// フィールド内の全デリゲート／state machine を辿って、本当の async メソッド名まで掘り出す。</summary>
    private static string DescribeOperation(DispatcherOperation op)
    {
        try
        {
            var found = new System.Collections.Generic.List<string>();
            var others = new System.Collections.Generic.List<string>();
            var seen = new System.Collections.Generic.HashSet<object>(ReferenceEqualityComparer.Instance);
            Unwrap(op, found, others, seen, depth: 0);
            if (found.Count > 0) return string.Join(" / ", found);
            // Loomo のメソッドに行き着かなくても、名前を捨てない——WPF 内部（TSF/IME、入力、
            // レイアウト）や他パッケージ由来の停止は「(不明)」と書かれると切り分け不能になる。
            return others.Count > 0 ? $"[外部] {string.Join(" / ", others)}" : "(不明)";
        }
        catch { return "(反射失敗)"; }
    }

    /// <summary>オブジェクトのフィールドを再帰的に辿り、Loomo のメソッド名／async state machine を集める。
    /// async 継続の本体（<c>&lt;Method&gt;d__NN</c>）は値型フィールドに埋まっているので、値型でも
    /// <c>d__</c> なら辿る。Loomo 以外のデリゲート名は <paramref name="others"/> へ控えておき、
    /// Loomo のものが一つも無かったときだけ使う（雑音にせず、かつ取りこぼさない）。</summary>
    private static void Unwrap(object? obj, System.Collections.Generic.List<string> found,
        System.Collections.Generic.List<string> others,
        System.Collections.Generic.HashSet<object> seen, int depth)
    {
        if (obj is null || depth > 6 || found.Count >= 4 || !seen.Add(obj))
            return;

        var t = obj.GetType();

        // async state machine（<Method>d__NN）に行き着いた＝本体メソッド確定。
        if (t.Name.Contains("d__"))
        {
            var name = $"async {t.FullName}";
            if (!found.Contains(name)) found.Add(name);
            // state machine 内の await 対象までは辿らない（本体が分かれば十分）。
            return;
        }

        if (obj is Delegate d)
        {
            var m = d.Method;
            var name = $"{m.DeclaringType?.FullName}.{m.Name}";
            if (name.Contains("sk0ya.Loomo"))
            {
                if (!found.Contains(name)) found.Add(name);
            }
            else if (others.Count < 2 && !others.Contains(name))
            {
                others.Add(name);
            }
            Unwrap(d.Target, found, others, seen, depth + 1);
            return;
        }

        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            var ft = f.FieldType;
            if (ft == typeof(string) || ft.IsPrimitive || ft.IsEnum || ft == typeof(object[]))
                continue;
            // WPF ビジュアル/依存関係オブジェクトへは潜らない（巨大グラフ＆雑音）。
            if (ft.Namespace?.StartsWith("System.Windows") == true && !ft.Name.Contains("d__"))
                continue;
            object? v;
            try { v = f.GetValue(obj); } catch { continue; }
            if (v is not null)
                Unwrap(v, found, others, seen, depth + 1);
        }
    }

    /// <summary>レンダーティックの間隔を計る（UI スレッド上で呼ばれる）。</summary>
    private static void OnRendering(object? sender, EventArgs e)
    {
        var now = Clock.ElapsedMilliseconds;
        if (_lastFrameMs >= 0)
        {
            var gap = now - _lastFrameMs;
            if (gap >= StallThresholdMs)
                Append($"{now,8} ms  FRAME  gap={gap,5} ms   （描画フレーム間隔。UI遅延が平穏ならレンダー/合成側）");
        }
        _lastFrameMs = now;
    }

    /// <summary>Dispatcher キューの往復遅延を測り続ける（バックグラウンドスレッド）。</summary>
    private static void PingLoop()
    {
        var dispatcher = _dispatcher!;
        while (_running)
        {
            // GC 回数はこの ping の<b>往復ぶんだけ</b>を数える。以前は「前回記録した時点から」の
            // 累計を出していたので、しきい値未満の平穏な時間に回った GC まで stall の数字に
            // 混ざっていた（gen0=171 のような、読み手を誤らせる値になる）。
            int g0Before = GC.CollectionCount(0);
            int g1Before = GC.CollectionCount(1);
            int g2Before = GC.CollectionCount(2);

            var sw = Stopwatch.StartNew();
            var done = new ManualResetEventSlim(false);
            try
            {
                dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(done.Set));
            }
            catch
            {
                done.Dispose();
                return; // Dispatcher 終了（アプリ終了）
            }

            if (!done.Wait(PingWaitCapMs))
            {
                // ここまで返らないのは事実上のハング。遅れて届く Set() と競合させないため、
                // この 1 個だけは破棄しない（破棄済みへの Set() は UI スレッド上で例外になる）。
                Append($"{Clock.ElapsedMilliseconds,8} ms  UIWAIT latency>{PingWaitCapMs,4} ms   （UI スレッドが返らない）");
                continue;
            }

            var latency = sw.ElapsedMilliseconds;
            done.Dispose();
            if (latency >= StallThresholdMs)
            {
                // GC の回数も添える。UI が止まる理由は「コードが占有した」か「GC で全スレッドが
                // 止まった」かで対処がまったく違う——前者はその処理を背景へ、後者は割り当てを減らす。
                // gen2 が増えていれば、大きな一時オブジェクト（LOH 行き）を作り続けている疑い。
                int g0 = GC.CollectionCount(0) - g0Before;
                int g1 = GC.CollectionCount(1) - g1Before;
                int g2 = GC.CollectionCount(2) - g2Before;
                var gc = (g0 | g1 | g2) != 0 ? $"  GC(gen0={g0} gen1={g1} gen2={g2})" : "";
                Append($"{Clock.ElapsedMilliseconds,8} ms  UIWAIT latency={latency,5} ms{gc}   （UIスレッド占有。ハンドラ/レイアウトが犯人）");
            }

            Thread.Sleep(PingIntervalMs);
        }
    }

    private static void TryTruncate()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.WriteAllText(LogPath,
                $"=== Loomo ジャンク計測 {DateTime.Now:yyyy-MM-dd HH:mm:ss}  (しきい値 {StallThresholdMs}ms) ===\n" +
                "UIWAIT が跳ねる＝UIスレッド占有 / FRAME だけ跳ねる＝レンダー・合成側のヒッチ\n");
        }
        catch { /* 計測は補助。失敗は無視 */ }
    }

    private static void Append(string line)
    {
        lock (Lock)
        {
            try { File.AppendAllText(LogPath, line + "\n"); }
            catch { /* 計測は補助。失敗は無視 */ }
        }
    }
}
