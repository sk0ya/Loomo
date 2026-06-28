using System.Collections.Generic;

namespace sk0ya.Loomo.Core.Debug;

/// <summary>デバッグセッションの状態。Phase 1 では Idle / Launching / Running / Terminated / Failed を使う
/// （<see cref="Stopped"/> はブレークポイントで停止した状態＝Phase 2 以降で利用）。</summary>
public enum DebugSessionState
{
    /// <summary>セッション無し。</summary>
    Idle,
    /// <summary>アダプタ起動・初期化中。</summary>
    Launching,
    /// <summary>対象プログラム実行中。</summary>
    Running,
    /// <summary>ブレークポイント等で停止中（Phase 2 以降）。</summary>
    Stopped,
    /// <summary>セッション終了。</summary>
    Terminated,
    /// <summary>アダプタ未導入・起動失敗などの異常終了。</summary>
    Failed,
}

/// <summary>デバッグ対象/アダプタからの出力種別（DAP の output イベントの category に対応）。</summary>
public enum DebugOutputCategory
{
    /// <summary>対象プログラムの標準出力。</summary>
    Stdout,
    /// <summary>対象プログラムの標準エラー。</summary>
    Stderr,
    /// <summary>デバッガ/アダプタ自身のメッセージ。</summary>
    Console,
    /// <summary>強調表示すべき重要メッセージ（起動・終了・エラー等、Loomo 側で付与）。</summary>
    Important,
}

/// <summary>デバッグコンソールに流す 1 行分の出力。</summary>
public sealed record DebugOutput(DebugOutputCategory Category, string Text);

/// <summary>セッション終了の通知。<see cref="ExitCode"/> は取得できない場合 null。</summary>
public sealed record DebugExited(int? ExitCode, string? Reason);

/// <summary>デバッガが停止した位置の通知（ブレークポイント・ステップ・例外など）。
/// <see cref="Line"/> は DAP 準拠で <b>1 始まり</b>（エディタのバッファ行へ渡す際に -1 する）。
/// <see cref="SourcePath"/> はソースの絶対パス（取得できなければ null）。</summary>
public sealed record DebugStopped(string? SourcePath, int Line, string Reason, int ThreadId);

/// <summary>コールスタックの 1 フレーム。<see cref="Id"/> は scopes/evaluate に渡す DAP frameId。
/// <see cref="Line"/> は 1 始まり。</summary>
public sealed record DebugStackFrame(int Id, string Name, string? SourcePath, int Line);

/// <summary>変数スコープ（Locals / Arguments など）。<see cref="VariablesReference"/> で変数一覧を引く。</summary>
public sealed record DebugScope(string Name, int VariablesReference, bool Expensive);

/// <summary>1 変数。<see cref="VariablesReference"/> が 0 より大きければ子（フィールド/要素）を展開できる。</summary>
public sealed record DebugVariable(string Name, string Value, string? Type, int VariablesReference);

/// <summary>実行中スレッド 1 つ（DAP の threads）。<see cref="Id"/> は stackTrace/step の対象。</summary>
public sealed record DebugThread(int Id, string Name);

/// <summary>ロード済みモジュール（アセンブリ）1 つ（DAP の <c>modules</c>）。<see cref="Path"/>/<see cref="Version"/> は
/// 取得できなければ null。<see cref="SymbolStatus"/> はシンボル（PDB）の読み込み状況（例「Symbols loaded.」）。</summary>
public sealed record DebugModule(string Name, string? Path, string? Version, string? SymbolStatus);

/// <summary>例外ブレークの 1 フィルタ（DAP の <c>exceptionBreakpointFilters</c>）。netcoredbg なら
/// 「すべての例外」「未捕捉例外」等。<see cref="Default"/> は初期 ON 推奨か。</summary>
public sealed record DebugExceptionFilter(string Id, string Label, bool Default);

/// <summary>1 ブレークポイント（行＋任意の条件）。<see cref="Line"/> は <b>1 始まり</b>（DAP 準拠）。
/// <see cref="Condition"/> は式が真のときだけ停止、<see cref="HitCondition"/> はヒット回数条件
/// （例 <c>5</c>＝5 回目、<c>&gt;=3</c>）、<see cref="LogMessage"/> は停止せずログ出力するログポイント
/// （<c>{式}</c> 展開）。<see cref="Enabled"/> が false の行はアダプタへ送らない。</summary>
public sealed record DebugBreakpoint(
    int Line,
    string? Condition = null,
    string? HitCondition = null,
    string? LogMessage = null,
    bool Enabled = true);

/// <summary>デバッグ起動構成（Phase 1：.NET プログラムを起動して実行・出力を観測する）。</summary>
public sealed record DebugLaunchConfig(
    /// <summary>実行する .NET アセンブリ（<c>*.dll</c>）または実行ファイルの絶対パス。</summary>
    string Program,
    /// <summary>作業ディレクトリ。null ならプログラムの置かれたフォルダ。</summary>
    string? WorkingDirectory = null,
    /// <summary>プログラムへ渡す引数。</summary>
    IReadOnlyList<string>? Args = null,
    /// <summary>環境変数（プロセス起動時に追加・上書きする）。null/空なら親プロセスの環境のまま。</summary>
    IReadOnlyDictionary<string, string>? Environment = null,
    /// <summary>エントリポイントで停止するか（Phase 2 以降のステップ実行用、Phase 1 では false 運用）。</summary>
    bool StopAtEntry = false,
    /// <summary>マイコードのみをデバッグするか（true なら外部/フレームワークコードへ降りない）。
    /// VS の「マイ コードのみ」。既定 false（全コードをステップ可能）。</summary>
    bool JustMyCode = false);

/// <summary>デバッグアタッチ構成。既に実行中の .NET プロセス（<see cref="ProcessId"/>）に接続する。
/// <see cref="Name"/> は表示用（出力に出すプロセス名、無くてもよい）。</summary>
public sealed record DebugAttachConfig(int ProcessId, string? Name = null);

/// <summary>ソース走査で見つかった 1 テスト（<c>dotnet test --list-tests</c> のビルドを伴わない高速探索の結果）。
/// <see cref="FullyQualifiedName"/> は <c>Namespace.Class.Method</c>（テオリでも引数なしのメソッド単位）。
/// <see cref="IsParameterized"/> は <c>[Theory]</c> 等の複数ケースを持つメソッドか（実行結果はメソッド単位に集約する）。</summary>
public sealed record DiscoveredTest(string FullyQualifiedName, bool IsParameterized);
