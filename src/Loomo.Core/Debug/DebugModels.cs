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

/// <summary>デバッグ起動構成（Phase 1：.NET プログラムを起動して実行・出力を観測する）。</summary>
public sealed record DebugLaunchConfig(
    /// <summary>実行する .NET アセンブリ（<c>*.dll</c>）または実行ファイルの絶対パス。</summary>
    string Program,
    /// <summary>作業ディレクトリ。null ならプログラムの置かれたフォルダ。</summary>
    string? WorkingDirectory = null,
    /// <summary>プログラムへ渡す引数。</summary>
    IReadOnlyList<string>? Args = null,
    /// <summary>エントリポイントで停止するか（Phase 2 以降のステップ実行用、Phase 1 では false 運用）。</summary>
    bool StopAtEntry = false);
