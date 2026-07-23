using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Debug;
using sk0ya.Loomo.Services.Debug;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>デバッグコンソールの 1 行。<see cref="Category"/> で色分けする（XAML 側のトリガ）。</summary>
public sealed class DebugOutputLine
{
    public DebugOutputLine(DebugOutputCategory category, string text)
    {
        Category = category;
        Text = text;
    }

    public DebugOutputCategory Category { get; }
    public string Text { get; }
}

/// <summary>
/// IDE（デバッグ）ペインのファサード ViewModel の toolchain 非依存な中核。<b>複数セッションのマネージャ</b>：
/// <see cref="Sessions"/> に実行中/停止中/終了直後のセッション（<see cref="DebugSessionViewModel"/>、
/// 1 つが 1 アダプタプロセス）をいくつも保持し、<see cref="ActiveSession"/> がどれをデバッグペインへ見せるかを
/// 選ぶ（Rider の「実行」ツールウィンドウで表示中セッションを切り替えるのと同じ発想）。
/// 出力・状態文言・実行中/停止中・検査（コールスタック等）はセッションごとに別だが、
/// <see cref="Breakpoints"/>（ファイル単位でグローバル共有）と <see cref="Problems"/> は全セッションで共有する。
/// toolchain 固有のサブ VM（起動・アタッチ・テスト等）と対象解決（<see cref="FindBuildTarget"/>）は派生
/// （dotnet: <see cref="DebugViewModel"/> / TypeScript: <see cref="TsDebugViewModel"/>）が持つ。
/// </summary>
public abstract partial class DebugManagerViewModelBase : ObservableObject, IDebugSession, IDisposable
{
    private readonly IDebugSessionFactory _sessionFactory;
    private readonly IDebugService _adapterProbe;
    private readonly ObservableCollection<DebugOutputLine> _fallbackOutput = new();
    private string _fallbackStatusMessage = "待機中";
    private DebugSessionViewModel? _activeSession;

    /// <summary>デバッグアダプタが未導入か（導入を促すバーの表示に使う）。全セッション共有（machine-wide の事実）。</summary>
    [ObservableProperty] private bool _isAdapterMissing;

    /// <summary>ビルド/テストを実行中か（デバッグの <see cref="IsBusy"/> とは別系統。全セッション共有の同時実行ゲート——
    /// ビルド中に別セッションを開始してファイルロックと競合しないようにする）。</summary>
    [ObservableProperty] private bool _isTaskRunning;

    /// <summary>現在保持している全セッション（実行中/停止中/終了直後）。</summary>
    public ObservableCollection<DebugSessionViewModel> Sessions { get; } = new();

    /// <summary>2 つ以上セッションがあるか（セッション切替 UI の表示要否）。</summary>
    public bool HasMultipleSessions => Sessions.Count > 1;

    /// <summary>デバッグペインが今見せているセッション。切替時に検査/出力/状態のバインディングを丸ごと差し替える。</summary>
    public DebugSessionViewModel? ActiveSession
    {
        get => _activeSession;
        set
        {
            if (ReferenceEquals(_activeSession, value)) return;
            DetachActiveSessionHandlers();
            _activeSession = value;
            AttachActiveSessionHandlers();
            OnPropertyChanged();
            OnPropertyChanged(nameof(Inspection));
            OnPropertyChanged(nameof(Output));
            OnPropertyChanged(nameof(StatusMessage));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsStopped));
            ExecutionLineChanged?.Invoke(null, -1);
            StoppedChanged?.Invoke(value?.IsStopped ?? false);
            SessionStateChanged?.Invoke();
        }
    }

    /// <summary>今アクティブなセッションの検査（コールスタック/変数/自動/ウォッチ/スレッド/イミディエイト/モジュール）。
    /// セッションが 1 つも無ければ null（構成タブのみ表示できる状態）。</summary>
    public DebugInspectionViewModel? Inspection => ActiveSession?.Inspection;

    /// <summary>アクティブなセッションのコンソール出力。セッションが無ければ、まだどれにも属さない共有出力
    /// （アダプタ未導入メッセージ・テスト実行の出力等）。</summary>
    public ObservableCollection<DebugOutputLine> Output => ActiveSession?.Output ?? _fallbackOutput;

    /// <summary>状態の短い説明（ヘッダ脇に表示）。アクティブなセッションのものを転送する。</summary>
    public string StatusMessage
    {
        get => ActiveSession?.StatusMessage ?? _fallbackStatusMessage;
        set
        {
            if (ActiveSession is { } s) s.StatusMessage = value;
            else if (_fallbackStatusMessage != value) { _fallbackStatusMessage = value; OnPropertyChanged(); }
        }
    }

    /// <summary>アクティブなセッションが起動中/実行中か（開始ボタンの可否・停止ボタンの表示に使う）。</summary>
    public bool IsBusy => ActiveSession?.IsBusy ?? false;

    /// <summary>アクティブなセッションがブレークポイント等で停止中か（ステップ/続行ボタンの表示・可否に使う）。</summary>
    public bool IsStopped => ActiveSession?.IsStopped ?? false;

    // --- サブ ViewModel（toolchain 非依存の共有分。固有分は派生が持つ） ---
    /// <summary>「問題」タブ（ビルド系コマンド出力のエラー/警告の抽出）。デバッグセッションに依存しない。
    /// 流し込みは各ビルド実行箇所が <see cref="ReportBuildOutput"/> で行う。</summary>
    public ProblemsViewModel Problems { get; }
    public DebugBreakpointsViewModel Breakpoints { get; }

    // --- エディタ連携の通知（ShellWindow が購読。ExecutionLineChanged 等はアクティブセッションの分を中継する） ---

    /// <summary>停止位置をエディタへ反映するための通知（path, 0始まり行）。行が -1 のときは実行行ハイライト解除。
    /// path が null のときは全エディタの実行行を解除する。</summary>
    public event Action<string?, int>? ExecutionLineChanged;

    /// <summary>停止/実行の切り替わり通知（true=停止）。エディタの DataTip（ホバー値表示）の有効化に使う。</summary>
    public event Action<bool>? StoppedChanged;

    /// <summary>コールスタックのフレーム選択でソースをプレビュー表示する要求（path, 0始まり行）。</summary>
    public event Action<string, int>? FramePreviewRequested;

    /// <summary>ソースへジャンプする要求（path, 0始まり行）。通常タブで開き、エディタにフォーカスする。</summary>
    public event Action<string, int>? FrameActivated;

    /// <summary>ブレークポイント一覧が（パネル操作で）変わったので、そのパスのエディタのガターを同期し直す要求。
    /// ブレークポイントはセッション非依存（ファイル単位でグローバル共有）なので、この通知もセッション非依存。</summary>
    public event Action<string>? BreakpointsRefreshed;

    /// <summary>実行系コマンド（開始/アタッチ/ビルド/テスト）を押した瞬間に「出力」タブを見せる要求。</summary>
    public event Action? OutputRequested;

    /// <summary>共有系サブ VM（Tests/Breakpoints）や、いずれかのセッションの実行中/停止中/タスク実行中が変わった
    /// ときに発火。各サブ VM がコマンドの可否を取り直す。</summary>
    public event Action? SessionStateChanged;

    protected DebugManagerViewModelBase(IDebugSessionFactory sessionFactory, IWorkspaceService workspace)
    {
        _sessionFactory = sessionFactory;
        _adapterProbe = sessionFactory.Create();

        Problems = new ProblemsViewModel(workspace);
        Breakpoints = new DebugBreakpointsViewModel(() => Sessions.Select(s => s.DebugService), this);

        Sessions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMultipleSessions));

        IsAdapterMissing = !_adapterProbe.IsAdapterAvailable;
    }

    /// <summary>パネルが開かれたときにアダプタ導入状況を取り直す（導入直後でも反映されるように）。</summary>
    public void Refresh() => IsAdapterMissing = !_adapterProbe.IsAdapterAvailable;

    /// <summary>ビルド/テスト対象を解決する。無ければ null（理由はコンソールへ）。toolchain 固有
    /// （dotnet: .sln/.csproj / TypeScript: tsconfig のあるディレクトリ）。</summary>
    public abstract string? FindBuildTarget();

    /// <summary>新しい（未起動の）セッションを作り、ブレークポイントの現在値を流し込んでアクティブにする。
    /// 起動/アタッチ系サブ VM が開始/アタッチのたびに呼ぶ。
    /// 既存セッションは止めない——これが「2 つ目を始めても 1 つ目は死なない」の核。</summary>
    internal DebugSessionViewModel CreateSession(string displayName, DebugSessionKind kind)
    {
        var debug = _sessionFactory.Create();
        var session = new DebugSessionViewModel(debug, this, DisambiguateSessionName(displayName)) { Kind = kind };
        Breakpoints.PrimeSession(debug);
        session.SessionStateChanged += OnAnySessionStateChanged;
        Sessions.Add(session);
        ActiveSession = session;
        return session;
    }

    private void OnAnySessionStateChanged() => SessionStateChanged?.Invoke();

    /// <summary>同じ対象を2回起動した場合など、既存セッションと表示名が重なるならセッション切替コンボで
    /// 見分けられるよう連番を振る（"MarsThrusterDemo" → "MarsThrusterDemo (2)"）。</summary>
    private string DisambiguateSessionName(string displayName)
    {
        if (Sessions.All(s => s.DisplayName != displayName)) return displayName;
        var n = 2;
        while (Sessions.Any(s => s.DisplayName == $"{displayName} ({n})")) n++;
        return $"{displayName} ({n})";
    }

    /// <summary>直前に終了したセッションのアダプタ後始末（dll/pdb のハンドル解放）が残っていれば、全セッション分待つ。
    /// ビルド前に呼ぶ（同じ出力 dll を使う可能性があるどのセッションの後始末とも競合しないように）。</summary>
    public async Task WaitForAllIdleAsync()
    {
        foreach (var s in Sessions.ToList())
            await s.DebugService.WaitForIdleAsync();
    }

    // --- コンソール出力（アクティブセッションへ委譲。無ければ共有のフォールバックへ） ---

    public void Append(DebugOutputCategory category, string text)
    {
        if (ActiveSession is { } s) ((IDebugSession)s).Append(category, text);
        else AppendFallback(category, text);
    }

    private void AppendFallback(DebugOutputCategory category, string text)
    {
        _fallbackOutput.Add(new DebugOutputLine(category, text));
        const int max = 2000;
        if (_fallbackOutput.Count > max) _fallbackOutput.RemoveAt(0);
    }

    /// <summary>複数行のコマンド出力を 1 行ずつコンソールへ流す（末尾の CR を落とし、空行は捨てる）。</summary>
    public void WriteConsole(string output)
    {
        if (ActiveSession is { } s) { s.WriteConsole(output); return; }
        foreach (var line in output.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (t.Length > 0) AppendFallback(DebugOutputCategory.Console, t);
        }
    }

    /// <summary>ビルド系コマンドの出力からエラー/警告を抽出して「問題」タブへ反映する。
    /// <paramref name="baseDir"/> は相対パス診断（tsc）の絶対化基準（dotnet は絶対パスなので null で可）。</summary>
    public void ReportBuildOutput(string output, string? baseDir = null) => Problems.SetFromBuildOutput(output, baseDir);

    public void RequestOutput() => OutputRequested?.Invoke();

    // --- アクティブセッションの切替配線 ---

    private void DetachActiveSessionHandlers()
    {
        if (_activeSession is null) return;
        _activeSession.PropertyChanged -= OnActiveSessionPropertyChanged;
        _activeSession.ExecutionLineChanged -= OnActiveExecutionLineChanged;
        _activeSession.FramePreviewRequested -= OnActiveFramePreviewRequested;
        _activeSession.FrameActivated -= OnActiveFrameActivated;
        _activeSession.StoppedChanged -= OnActiveStoppedChanged;
    }

    private void AttachActiveSessionHandlers()
    {
        if (_activeSession is null) return;
        _activeSession.PropertyChanged += OnActiveSessionPropertyChanged;
        _activeSession.ExecutionLineChanged += OnActiveExecutionLineChanged;
        _activeSession.FramePreviewRequested += OnActiveFramePreviewRequested;
        _activeSession.FrameActivated += OnActiveFrameActivated;
        _activeSession.StoppedChanged += OnActiveStoppedChanged;
    }

    private void OnActiveSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DebugSessionViewModel.StatusMessage): OnPropertyChanged(nameof(StatusMessage)); break;
            case nameof(DebugSessionViewModel.IsBusy): OnPropertyChanged(nameof(IsBusy)); break;
        }
    }

    private void OnActiveExecutionLineChanged(string? path, int line0) => ExecutionLineChanged?.Invoke(path, line0);
    private void OnActiveFramePreviewRequested(string path, int line0) => FramePreviewRequested?.Invoke(path, line0);
    private void OnActiveFrameActivated(string path, int line0) => FrameActivated?.Invoke(path, line0);
    private void OnActiveStoppedChanged(bool stopped)
    {
        OnPropertyChanged(nameof(IsStopped));
        StoppedChanged?.Invoke(stopped);
    }

    /// <summary>「次のステートメントに設定」等、共有の起動系サブ VM からアクティブセッションの実行行を
    /// 直接動かす経路（DAP イベント経由でない反映）。</summary>
    internal void RaiseActiveExecutionLine(string path, int line0) => ActiveSession?.NotifyExecutionLine(path, line0);

    // --- IDebugSession 実装（Breakpoints/Tests が共有の窓口として使う） ---

    bool IDebugSession.IsBusy => Sessions.Any(s => s.IsBusy);
    bool IDebugSession.IsStopped => Sessions.Any(s => s.IsStopped);
    bool IDebugSession.IsTaskRunning { get => IsTaskRunning; set => IsTaskRunning = value; }
    bool IDebugSession.IsAdapterMissing => IsAdapterMissing;
    string IDebugSession.StatusMessage { get => StatusMessage; set => StatusMessage = value; }

    partial void OnIsTaskRunningChanged(bool value) => SessionStateChanged?.Invoke();

    void IDebugSession.RefreshAdapter() => Refresh();

    // 開始/停止操作の CancellationToken はセッションごとに個別に持つため、マネージャ自身では未使用。
    System.Threading.CancellationToken IDebugSession.BeginSession() => new System.Threading.CancellationTokenSource().Token;
    void IDebugSession.CancelSession() { }

    void IDebugSession.RequestOutput() => RequestOutput();
    void IDebugSession.RaiseExecutionLine(string? path, int line0)
    {
        if (path is not null) RaiseActiveExecutionLine(path, line0);
        else ExecutionLineChanged?.Invoke(null, line0);
    }
    void IDebugSession.RaiseFramePreview(string path, int line0) => ActiveSession?.NotifyFramePreview(path, line0);
    void IDebugSession.RaiseFrameActivated(string path, int line0) => FrameActivated?.Invoke(path, line0);
    void IDebugSession.RaiseBreakpointsRefreshed(string path) => BreakpointsRefreshed?.Invoke(path);
    void IDebugSession.Append(DebugOutputCategory category, string text) => Append(category, text);
    void IDebugSession.ReportBuildOutput(string output) => ReportBuildOutput(output);
    string? IDebugSession.FindBuildTarget() => FindBuildTarget();

    public virtual void Dispose()
    {
        foreach (var s in Sessions) s.Dispose();
    }
}
