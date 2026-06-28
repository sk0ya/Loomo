using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Core.Debug;

/// <summary>
/// デバッグ実行（DAP: Debug Adapter Protocol）への操作を抽象化する。<see cref="ITerminalService"/> 等と同様に
/// Core は UI 非依存で、具体的なアダプタ駆動（stdio・プロセス管理）は Services 層に置く。
///
/// Phase 1 は「起動して実行し、標準出力/標準エラー/終了を観測する」までを担う（ブレークポイント・変数・
/// ステップ実行は Phase 2 以降で同じ抽象を拡張する）。アダプタ（C# は <c>netcoredbg</c>）が PATH 上に
/// 無い場合は <see cref="IsAdapterAvailable"/> が false になり、<see cref="StartAsync"/> は状態を
/// <see cref="DebugSessionState.Failed"/> にして導入を促す（LSP と同じ「PATH にあれば使える」方式）。
/// </summary>
public interface IDebugService
{
    /// <summary>現在のセッション状態。</summary>
    DebugSessionState State { get; }

    /// <summary>デバッグアダプタ（netcoredbg）が PATH 上で解決でき、デバッグ起動が可能か。</summary>
    bool IsAdapterAvailable { get; }

    /// <summary>アダプタが <c>setVariable</c>（変数値の書き換え）に対応しているか。<see cref="StartAsync"/>/
    /// <see cref="AttachAsync"/> の initialize 応答で確定する（未起動時は false）。</summary>
    bool SupportsSetVariable { get; }

    /// <summary>アダプタが <c>gotoTargets</c>/<c>goto</c>（次に実行する文の移動）に対応しているか。</summary>
    bool SupportsSetNextStatement { get; }

    /// <summary>アダプタが申告した例外ブレークのフィルタ一覧（initialize 応答の
    /// <c>exceptionBreakpointFilters</c>）。空ならアダプタは例外ブレークを申告していない。</summary>
    IReadOnlyList<DebugExceptionFilter> ExceptionFilters { get; }

    /// <summary>現在アクティブな（stackTrace/step の対象となる）スレッド ID。停止時にその停止スレッドへ更新される。</summary>
    int ActiveThreadId { get; }

    /// <summary>指定構成でデバッグ起動する。既存セッションがあれば先に停止する。
    /// アダプタ未導入・起動失敗時は例外を投げず、状態を <see cref="DebugSessionState.Failed"/> にして
    /// <see cref="Output"/> に理由を流す。</summary>
    Task StartAsync(DebugLaunchConfig config, CancellationToken ct);

    /// <summary>実行中の .NET プロセスにアタッチしてデバッグする。既存セッションがあれば先に停止する。
    /// アダプタ未導入・接続失敗時は例外を投げず、状態を <see cref="DebugSessionState.Failed"/> にして
    /// <see cref="Output"/> に理由を流す。停止（disconnect）してもアタッチ先プロセスは終了させない。</summary>
    Task AttachAsync(DebugAttachConfig config, CancellationToken ct);

    /// <summary>実行中のセッションを停止（disconnect/terminate）する。セッションが無ければ何もしない。</summary>
    Task StopAsync();

    /// <summary>あるソースファイルのブレークポイント（<b>1 始まり</b>の行＋任意の条件）をまとめて設定する。
    /// セッション開始前に呼ばれた分は記憶し、起動時の構成フェーズで送る。実行中の呼び出しは即時反映する。
    /// <see cref="DebugBreakpoint.Enabled"/> が false の行は送らない。</summary>
    Task SetBreakpointsAsync(string sourcePath, IReadOnlyList<DebugBreakpoint> breakpoints, CancellationToken ct);

    /// <summary>例外ブレークのフィルタを設定する（<paramref name="filterIds"/> は <see cref="ExceptionFilters"/> の
    /// <see cref="DebugExceptionFilter.Id"/>）。実行中なら即時、未起動なら構成フェーズで送る。</summary>
    Task SetExceptionBreakpointsAsync(IReadOnlyList<string> filterIds, CancellationToken ct);

    /// <summary>変数値を書き換える（<c>setVariable</c>）。<paramref name="variablesReference"/> はその変数が属する
    /// スコープ/親の参照。成功時は新しい表示値、失敗時は null を返す（例外は投げない）。</summary>
    Task<string?> SetVariableAsync(int variablesReference, string name, string value);

    /// <summary>実行中スレッドの一覧を取得する（取得できなければ空）。</summary>
    Task<IReadOnlyList<DebugThread>> GetThreadsAsync();

    /// <summary>stackTrace/step の対象スレッドを切り替える（停止中のみ意味を持つ）。</summary>
    void SetActiveThread(int threadId);

    /// <summary>実行中のセッションを一時停止する（<c>pause</c>＝VS の「すべて中断」）。停止すると
    /// <see cref="Stopped"/> が発火する。</summary>
    Task PauseAsync();

    /// <summary>次に実行する文を指定位置へ移動する（<c>gotoTargets</c>→<c>goto</c>＝VS の「次のステートメントの設定」）。
    /// 停止中のみ有効。<paramref name="line"/> は <b>1 始まり</b>。成功可否を返す（失敗理由は <see cref="Output"/>）。</summary>
    Task<bool> SetNextStatementAsync(string sourcePath, int line);

    /// <summary>停止中のセッションを再開する。</summary>
    Task ContinueAsync();

    /// <summary>ステップオーバー（next）。</summary>
    Task StepOverAsync();

    /// <summary>ステップイン。</summary>
    Task StepInAsync();

    /// <summary>ステップアウト。</summary>
    Task StepOutAsync();

    /// <summary>停止中スレッドのコールスタックを取得する（停止していなければ空）。</summary>
    Task<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync();

    /// <summary>指定スレッドのコールスタックを取得する。</summary>
    Task<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(int threadId);

    /// <summary>指定フレームのスコープ（Locals/Arguments 等）を取得する。</summary>
    Task<IReadOnlyList<DebugScope>> GetScopesAsync(int frameId);

    /// <summary>指定 variablesReference の変数一覧（スコープ直下、または親変数の子）を取得する。</summary>
    Task<IReadOnlyList<DebugVariable>> GetVariablesAsync(int variablesReference);

    /// <summary>式を評価して結果文字列を返す（ウォッチ）。<paramref name="frameId"/> 指定でそのフレーム文脈。
    /// 失敗時はエラーメッセージを返す（例外は投げない）。</summary>
    Task<string> EvaluateAsync(string expression, int? frameId);

    /// <summary>状態が変化したときに発火（UI スレッドとは限らない）。</summary>
    event EventHandler<DebugSessionState>? StateChanged;

    /// <summary>デバッグコンソールへ流す出力 1 行ごとに発火。</summary>
    event EventHandler<DebugOutput>? Output;

    /// <summary>ブレークポイント/ステップ/例外などでデバッガが停止したときに発火。</summary>
    event EventHandler<DebugStopped>? Stopped;

    /// <summary>停止状態から実行を再開したときに発火（実行中行ハイライトの解除に使う）。</summary>
    event EventHandler? Continued;

    /// <summary>セッションが終了したときに発火。</summary>
    event EventHandler<DebugExited>? Exited;
}
