using System;
using System.Threading;
using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>デバッグペインのサブ ViewModel（起動・テスト・検査・ブレークポイント・アタッチ）が共有する
/// セッション状態とコンソール・エディタ連携の窓口。<see cref="DebugViewModel"/> が実装し、各サブ VM に渡す。
/// 状態の真実（実行中/停止中/タスク実行中・状態文言・出力）はファサードが一元的に持つ。</summary>
internal interface IDebugSession
{
    /// <summary>セッションが起動中/実行中か。</summary>
    bool IsBusy { get; }

    /// <summary>ブレークポイント等で停止中か。</summary>
    bool IsStopped { get; }

    /// <summary>ビルド/テストを実行中か（デバッグの <see cref="IsBusy"/> とは別系統の同時実行ゲート）。</summary>
    bool IsTaskRunning { get; set; }

    /// <summary>デバッグアダプタ（netcoredbg）が未導入か。</summary>
    bool IsAdapterMissing { get; }

    /// <summary>状態の短い説明（ヘッダ脇に表示）。</summary>
    string StatusMessage { get; set; }

    /// <summary>アダプタ導入状況を取り直す（導入直後でも反映されるように）。</summary>
    void RefreshAdapter();

    /// <summary>新しいデバッグセッションを開始し、その停止操作用のキャンセルトークンを返す（起動/アタッチ共通）。</summary>
    CancellationToken BeginSession();

    /// <summary>進行中のセッション開始操作をキャンセルする（停止操作）。</summary>
    void CancelSession();

    /// <summary>コンソールへ 1 行追記する。</summary>
    void Append(DebugOutputCategory category, string text);

    /// <summary>複数行のコマンド出力を 1 行ずつコンソールへ流す（末尾 CR を落とし空行は捨てる）。</summary>
    void WriteConsole(string output);

    /// <summary>実行系コマンド押下時に「出力」タブを即表示する要求を発火する。</summary>
    void RequestOutput();

    /// <summary>ビルド/テスト対象（.sln 優先、無ければ最初の .csproj）を解決する。無ければ null（理由はコンソールへ）。</summary>
    string? FindBuildTarget();

    /// <summary>停止位置をエディタへ反映する（path, 0始まり行）。行が -1／path が null は実行行ハイライト解除。</summary>
    void RaiseExecutionLine(string? path, int line0);

    /// <summary>ソース位置をエディタにプレビュー表示する要求（path, 0始まり行）。フォーカスは奪わない。</summary>
    void RaiseFramePreview(string path, int line0);

    /// <summary>ソースへジャンプする要求（path, 0始まり行）。通常タブで開きフォーカスする。</summary>
    void RaiseFrameActivated(string path, int line0);

    /// <summary>そのパスのエディタのガターをブレークポイント一覧に同期し直す要求。</summary>
    void RaiseBreakpointsRefreshed(string path);

    /// <summary>実行中/停止中/タスク実行中のいずれかが変わったときに発火（各サブ VM がコマンドの可否を取り直す）。</summary>
    event Action? SessionStateChanged;
}
