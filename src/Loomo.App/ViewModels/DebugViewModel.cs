using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

/// <summary>アタッチ候補となる実行中プロセスの 1 行。<see cref="IsManaged"/> は coreclr 等のロードを検出できたか
/// （検出できなければ未管理扱い。アクセス権限不足や 32bit 不一致で判定不能なものも未管理側になる）。</summary>
public sealed class DebugProcessViewModel
{
    public DebugProcessViewModel(int pid, string name, string? title, bool isManaged)
    {
        Pid = pid;
        Name = name;
        Title = title;
        IsManaged = isManaged;
    }

    public int Pid { get; }
    public string Name { get; }
    public string? Title { get; }
    public bool IsManaged { get; }

    /// <summary>リスト表示用（名前 (PID) — ウィンドウタイトル）。</summary>
    public string Display => string.IsNullOrEmpty(Title) ? $"{Name} ({Pid})" : $"{Name} ({Pid}) — {Title}";
}

/// <summary>
/// サイドバー「デバッグ」パネルの ViewModel（Phase 1）。ワークスペースの .NET プロジェクトを（任意でビルドしてから）
/// <see cref="IDebugService"/> 経由で netcoredbg 上でデバッグ起動し、標準出力/標準エラー/終了をコンソールに流す。
/// ブレークポイント・変数・ステップ実行は Phase 2 以降。
/// </summary>
public sealed partial class DebugViewModel : ObservableObject, IDisposable
{
    private readonly IDebugService _debug;
    private readonly IWorkspaceService _workspace;
    private readonly ITerminalService _terminal;
    private readonly ITestDiscoveryService _testDiscovery;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _cts;

    /// <summary>ソース変更を監視してテストを自動再収集するための監視（ルートごとに張り替える）。</summary>
    private FileSystemWatcher? _testWatcher;
    /// <summary>変更通知をまとめる遅延タイマ（編集中の連続通知で何度も走らせない）。</summary>
    private DispatcherTimer? _discoverDebounce;
    /// <summary>探索中に来た再収集要求（探索完了後にもう一度走らせる）。</summary>
    private bool _rediscoverRequested;

    /// <summary>デバッグ対象（<c>*.dll</c>/<c>*.exe</c>）の明示指定。空ならワークスペースから自動検出する。</summary>
    [ObservableProperty] private string _targetProgram = "";

    /// <summary>起動前に <c>dotnet build</c> を実行するか。</summary>
    [ObservableProperty] private bool _buildFirst = true;

    /// <summary>プログラムへ渡すコマンドライン引数（空白区切り・二重引用符でグループ化）。空なら引数なし。</summary>
    [ObservableProperty] private string _launchArgs = "";

    /// <summary>起動時に追加する環境変数（1 行 1 件 <c>KEY=VALUE</c>）。空なら親プロセスの環境のまま。</summary>
    [ObservableProperty] private string _launchEnv = "";

    /// <summary>状態の短い説明（ヘッダ脇に表示）。</summary>
    [ObservableProperty] private string _statusMessage = "待機中";

    /// <summary>セッションが起動中/実行中か（開始ボタンの可否・停止ボタンの表示に使う）。</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>デバッグアダプタ（netcoredbg）が未導入か（導入を促すバーの表示に使う）。</summary>
    [ObservableProperty] private bool _isAdapterMissing;

    /// <summary>ブレークポイント等で停止中か（ステップ/続行ボタンの表示・可否に使う）。</summary>
    [ObservableProperty] private bool _isStopped;

    /// <summary>ビルド/テストを実行中か（デバッグの <see cref="IsBusy"/> とは別系統。同時実行を防ぐゲート）。</summary>
    [ObservableProperty] private bool _isTaskRunning;

    /// <summary>直近のテスト実行の集計（成功/失敗/スキップ/合計）。テストタブのヘッダに表示する。</summary>
    [ObservableProperty] private string _testSummary = "";

    /// <summary>テスト結果が 1 度でも得られたか（テストタブの案内文の出し分けに使う）。</summary>
    [ObservableProperty] private bool _hasTestResults;

    /// <summary>バックグラウンドのテスト収集を実行中か（収集中インジケータと空状態の案内文に使う）。</summary>
    [ObservableProperty] private bool _isDiscoveringTests;

    /// <summary>テストがまだ無いときの案内文（収集中かどうかで出し分ける）。</summary>
    public string TestEmptyHint => IsDiscoveringTests
        ? "テストを収集しています…"
        : "テストが見つかりませんでした（ソース変更で自動収集します）。";

    /// <summary>絞り込み：成功したテストを表示するか（チェックを外すと隠す）。</summary>
    [ObservableProperty] private bool _showPassed = true;

    /// <summary>絞り込み：失敗したテストを表示するか。</summary>
    [ObservableProperty] private bool _showFailed = true;

    /// <summary>絞り込み：未実施（探索だけ／スキップ）のテストを表示するか。</summary>
    [ObservableProperty] private bool _showNotRun = true;

    /// <summary>テスト名の絞り込み文字列（完全名に含むで照合・大小無視）。</summary>
    [ObservableProperty] private string _testFilter = "";

    /// <summary>フィルタ適用後に表示できるテストが 1 件でもあるか（ツリー再構築で更新）。</summary>
    [ObservableProperty] private bool _hasVisibleTests;

    /// <summary>テストはあるがフィルタで全て隠れているか（「該当なし」案内の出し分け）。</summary>
    public bool NoFilterMatch => HasTestResults && !HasVisibleTests;

    partial void OnShowPassedChanged(bool value) => SyncTree();
    partial void OnShowFailedChanged(bool value) => SyncTree();
    partial void OnShowNotRunChanged(bool value) => SyncTree();
    partial void OnTestFilterChanged(string value) => SyncTree();
    partial void OnHasVisibleTestsChanged(bool value) => OnPropertyChanged(nameof(NoFilterMatch));
    partial void OnHasTestResultsChanged(bool value) => OnPropertyChanged(nameof(NoFilterMatch));

    /// <summary>テスト一覧（フラットな全件。突き合わせ・集計の元データ）。表示は <see cref="TestTree"/>。</summary>
    public ObservableCollection<TestItemViewModel> Tests { get; } = new();

    /// <summary>クラス単位にまとめたテストツリー（TreeView の表示元）。<see cref="SyncTree"/> で再構築する。</summary>
    public ObservableCollection<TestGroupViewModel> TestTree { get; } = new();

    /// <summary>ソースパス（絶対）→ そのファイルのブレークポイント行（BreakpointViewModel）。
    /// 行・条件・有効フラグの真実。エディタのガター表示と管理パネルの両方の元データ。</summary>
    private readonly Dictionary<string, List<BreakpointViewModel>> _breakpoints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>全ブレークポイントのフラット一覧（管理パネルの表示元）。</summary>
    public ObservableCollection<BreakpointViewModel> Breakpoints { get; } = new();

    /// <summary>ブレークポイント一覧が（パネル操作で）変わったので、そのパスのエディタのガターを同期し直す要求。</summary>
    public event Action<string>? BreakpointsRefreshed;

    /// <summary>ブレークポイントが 1 件でもあるか（パネルの空案内の出し分け）。</summary>
    [ObservableProperty] private bool _hasBreakpoints;

    /// <summary>停止位置をエディタへ反映するための通知（path, 0始まり行）。行が -1 のときは実行行ハイライト解除。
    /// path が null のときは全エディタの実行行を解除する。</summary>
    public event Action<string?, int>? ExecutionLineChanged;

    /// <summary>コールスタックのフレーム選択でソースをプレビュー表示する要求（path, 0始まり行）。
    /// プレビュータブを使い回し、エディタにフォーカスは奪わない。</summary>
    public event Action<string, int>? FramePreviewRequested;

    /// <summary>コールスタックのフレームのダブルクリックでソースへジャンプする要求（path, 0始まり行）。
    /// 通常タブで開き、エディタにフォーカスする。</summary>
    public event Action<string, int>? FrameActivated;

    /// <summary>停止中のコールスタック。</summary>
    public ObservableCollection<DebugFrameViewModel> CallStack { get; } = new();

    /// <summary>選択中フレーム（変更で変数を読み直す）。</summary>
    [ObservableProperty] private DebugFrameViewModel? _selectedFrame;

    /// <summary>変数ツリー（トップ階層はスコープ＝Locals 等、展開で変数→子フィールド）。</summary>
    public ObservableCollection<DebugVariableViewModel> Variables { get; } = new();

    /// <summary>自動変数（Autos）。停止行とその直前行で使われている変数だけを近似表示する（VS の「自動」）。
    /// netcoredbg は autos スコープを持たないので、ソースから識別子を拾って評価したベストエフォート。</summary>
    public ObservableCollection<WatchItemViewModel> Autos { get; } = new();

    /// <summary>自動変数が 1 件でもあるか（空案内の出し分け）。</summary>
    [ObservableProperty] private bool _hasAutos;

    /// <summary>ウォッチ式。</summary>
    public ObservableCollection<WatchItemViewModel> Watches { get; } = new();

    /// <summary>ウォッチ追加欄。</summary>
    [ObservableProperty] private string _watchExpression = "";

    /// <summary>実行中スレッド一覧（停止時に取得）。選択でアクティブスレッドを切り替える。</summary>
    public ObservableCollection<DebugThread> Threads { get; } = new();

    /// <summary>選択中スレッド（切替で stackTrace/step の対象を変える）。</summary>
    [ObservableProperty] private DebugThread? _selectedThread;

    /// <summary>スレッド一覧をプログラムから差し替え中（選択変更の再入で再読込しないためのガード）。</summary>
    private bool _settingThread;

    /// <summary>例外ブレーク：スローされたすべての例外で中断（netcoredbg フィルタ <c>all</c>）。</summary>
    [ObservableProperty] private bool _breakOnAllExceptions;

    /// <summary>例外ブレーク：未処理（ユーザーコード外へ抜ける）例外で中断（フィルタ <c>user-unhandled</c>）。</summary>
    [ObservableProperty] private bool _breakOnUncaughtExceptions;

    /// <summary>マイコードのみをデバッグするか（VS の「マイ コードのみ」）。次回起動から反映。</summary>
    [ObservableProperty] private bool _justMyCode;

    partial void OnBreakOnAllExceptionsChanged(bool value) => _ = ApplyExceptionFiltersAsync();
    partial void OnBreakOnUncaughtExceptionsChanged(bool value) => _ = ApplyExceptionFiltersAsync();

    /// <summary>例外ブレークのフィルタ選択をアダプタへ反映する（未起動でも記憶され、起動時に送られる）。</summary>
    private Task ApplyExceptionFiltersAsync()
    {
        var ids = new List<string>();
        if (BreakOnAllExceptions) ids.Add("all");
        if (BreakOnUncaughtExceptions) ids.Add("user-unhandled");
        return _debug.SetExceptionBreakpointsAsync(ids, CancellationToken.None);
    }

    /// <summary>netcoredbg の導入コマンド（促しバーのボタン用）。</summary>
    public string AdapterInstallCommand => DebugAdapterCatalog.Netcoredbg.InstallCommand ?? "";

    /// <summary>コンソール出力。</summary>
    public ObservableCollection<DebugOutputLine> Output { get; } = new();

    /// <summary>アタッチのプロセス選択パネルを開いているか。</summary>
    [ObservableProperty] private bool _showAttach;

    /// <summary>プロセス一覧の絞り込み文字列（名前・PID・タイトルに前方一致でなく含むで照合）。</summary>
    [ObservableProperty] private string _processFilter = "";

    /// <summary>.NET（coreclr 検出）プロセスのみ表示するか。アタッチ可能なのは基本これらのみ。</summary>
    [ObservableProperty] private bool _dotnetOnly = true;

    /// <summary>プロセス一覧を取得中か（更新ボタンの無効化に使う）。</summary>
    [ObservableProperty] private bool _isEnumeratingProcesses;

    /// <summary>選択中のアタッチ対象プロセス。</summary>
    [ObservableProperty] private DebugProcessViewModel? _selectedProcess;

    /// <summary>絞り込み後に表示するプロセス一覧。</summary>
    public ObservableCollection<DebugProcessViewModel> Processes { get; } = new();

    /// <summary>最後に列挙した全プロセス（絞り込み前）。フィルタ変更時はこれを再フィルタする。</summary>
    private IReadOnlyList<DebugProcessViewModel> _allProcesses = Array.Empty<DebugProcessViewModel>();

    public DebugViewModel(IDebugService debug, IWorkspaceService workspace, ITerminalService terminal,
        ITestDiscoveryService testDiscovery)
    {
        _debug = debug;
        _workspace = workspace;
        _terminal = terminal;
        _testDiscovery = testDiscovery;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _debug.Output += OnOutput;
        _debug.StateChanged += OnStateChanged;
        _debug.Stopped += OnStopped;
        _debug.Continued += OnContinued;
        _debug.Exited += OnDebugExited;

        IsAdapterMissing = !_debug.IsAdapterAvailable;

        // テストはバックグラウンドで自動収集する（ボタン契機を廃止）。ワークスペースを開いた時点と
        // ソース変更（監視）を契機に、ビルドを伴わない高速探索で一覧を更新する。
        _workspace.RootChanged += OnWorkspaceRootChanged;
        SetupTestWatcher(_workspace.RootPath);
        if (_workspace.RootPath is not null) _ = DiscoverTestsAsync();
    }

    /// <summary>あるファイルのブレークポイントをトグルする（行は 0 始まり）。新しい行集合を返す。
    /// デバッグアダプタへも（1 始まりへ変換して）反映する。</summary>
    public IReadOnlyList<int> ToggleBreakpoint(string sourcePath, int line0)
    {
        var path = Path.GetFullPath(sourcePath);
        if (!_breakpoints.TryGetValue(path, out var list))
            _breakpoints[path] = list = new List<BreakpointViewModel>();

        var existing = list.FirstOrDefault(b => b.Line0 == line0);
        if (existing is not null)
        {
            list.Remove(existing);
            Breakpoints.Remove(existing);
        }
        else
        {
            var bp = NewBreakpoint(path, line0);
            list.Add(bp);
            Breakpoints.Add(bp);
        }
        if (list.Count == 0) _breakpoints.Remove(path);

        PushBreakpoints(path);
        RefreshBreakpointFlags();
        return BreakpointLines(path);
    }

    /// <summary>あるファイルのブレークポイント行（0 始まり）を返す（エディタ同期用）。条件・有効に関わらず全行。</summary>
    public IReadOnlyList<int> GetBreakpoints(string sourcePath) => BreakpointLines(Path.GetFullPath(sourcePath));

    private IReadOnlyList<int> BreakpointLines(string fullPath)
        => _breakpoints.TryGetValue(fullPath, out var list)
            ? list.Select(b => b.Line0).OrderBy(l => l).ToList()
            : Array.Empty<int>();

    /// <summary>新しいブレークポイント行 VM を作り、条件変更時の再送を配線する。</summary>
    private BreakpointViewModel NewBreakpoint(string path, int line0)
    {
        var bp = new BreakpointViewModel(path, line0);
        bp.Changed += OnBreakpointChanged;
        return bp;
    }

    private void OnBreakpointChanged(BreakpointViewModel bp) => PushBreakpoints(bp.Path);

    /// <summary>そのファイルのブレークポイント（条件込み）をアダプタへ送る。</summary>
    private void PushBreakpoints(string path)
    {
        var models = _breakpoints.TryGetValue(path, out var list)
            ? list.OrderBy(b => b.Line0).Select(b => b.ToModel()).ToList()
            : new List<DebugBreakpoint>();
        _ = _debug.SetBreakpointsAsync(path, models, CancellationToken.None);
    }

    /// <summary>1 件のブレークポイントを削除する（管理パネルの ✕）。エディタのガターも同期し直す。</summary>
    [RelayCommand]
    private void RemoveBreakpoint(BreakpointViewModel? bp)
    {
        if (bp is null) return;
        if (_breakpoints.TryGetValue(bp.Path, out var list))
        {
            list.Remove(bp);
            if (list.Count == 0) _breakpoints.Remove(bp.Path);
        }
        Breakpoints.Remove(bp);
        PushBreakpoints(bp.Path);
        RefreshBreakpointFlags();
        BreakpointsRefreshed?.Invoke(bp.Path);
    }

    /// <summary>すべてのブレークポイントを削除する。</summary>
    [RelayCommand]
    private void RemoveAllBreakpoints()
    {
        var paths = _breakpoints.Keys.ToList();
        _breakpoints.Clear();
        Breakpoints.Clear();
        foreach (var p in paths) { PushBreakpoints(p); BreakpointsRefreshed?.Invoke(p); }
        RefreshBreakpointFlags();
    }

    private void RefreshBreakpointFlags() => HasBreakpoints = Breakpoints.Count > 0;

    private bool CanStep() => IsStopped;

    [RelayCommand(CanExecute = nameof(CanStep))]
    private async Task Continue() => await _debug.ContinueAsync();

    [RelayCommand(CanExecute = nameof(CanStep))]
    private async Task StepOver() => await _debug.StepOverAsync();

    [RelayCommand(CanExecute = nameof(CanStep))]
    private async Task StepInto() => await _debug.StepInAsync();

    [RelayCommand(CanExecute = nameof(CanStep))]
    private async Task StepOut() => await _debug.StepOutAsync();

    /// <summary>実行中（停止していない）ときだけ一時停止できる。</summary>
    private bool CanPause() => IsBusy && !IsStopped;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private async Task Pause() => await _debug.PauseAsync();

    private bool CanRestart() => IsBusy;

    /// <summary>セッションを停止して同じ対象で再起動する（直前が launch なら再 launch、attach なら再 attach）。</summary>
    [RelayCommand(CanExecute = nameof(CanRestart))]
    private async Task Restart()
    {
        var attach = _lastAttachProcess;
        await StopAsync();
        if (attach is not null) await AttachToAsync(attach);
        else await StartAsync();
    }

    /// <summary>直前のセッションがアタッチだったときの対象プロセス（再起動で再アタッチする）。launch なら null。</summary>
    private DebugProcessViewModel? _lastAttachProcess;

    private void OnStopped(object? sender, DebugStopped e)
        => _dispatcher.InvokeAsync(async () =>
        {
            if (!string.IsNullOrEmpty(e.SourcePath) && e.Line > 0)
            {
                Append(DebugOutputCategory.Important,
                    $"停止（{e.Reason}）: {Path.GetFileName(e.SourcePath)}:{e.Line}");
                ExecutionLineChanged?.Invoke(e.SourcePath, e.Line - 1);  // DAP 1始まり → エディタ 0始まり
            }
            else
            {
                Append(DebugOutputCategory.Important, $"停止（{e.Reason}）");
            }
            await LoadThreadsAsync();
            await LoadStackAsync();
            await LoadModulesAsync();
        });

    private void OnContinued(object? sender, EventArgs e)
        => _dispatcher.InvokeAsync(() => { ExecutionLineChanged?.Invoke(null, -1); ClearInspection(); });

    private void OnDebugExited(object? sender, DebugExited e)
        => _dispatcher.InvokeAsync(() => { ExecutionLineChanged?.Invoke(null, -1); ClearInspection(); });

    /// <summary>停止時：コールスタックを取得し、先頭フレームを選ぶ（→変数読込）。</summary>
    private async Task LoadStackAsync()
    {
        var frames = await _debug.GetStackTraceAsync();
        CallStack.Clear();
        foreach (var f in frames) CallStack.Add(new DebugFrameViewModel(f));
        SelectedFrame = CallStack.Count > 0 ? CallStack[0] : null;  // → OnSelectedFrameChanged が変数を読む
    }

    /// <summary>停止時：実行中スレッドを取得し、アクティブスレッドを選択状態にする。</summary>
    private async Task LoadThreadsAsync()
    {
        var threads = await _debug.GetThreadsAsync();
        _settingThread = true;
        try
        {
            Threads.Clear();
            foreach (var t in threads) Threads.Add(t);
            SelectedThread = Threads.FirstOrDefault(t => t.Id == _debug.ActiveThreadId) ?? Threads.FirstOrDefault();
        }
        finally { _settingThread = false; }
    }

    partial void OnSelectedThreadChanged(DebugThread? value)
    {
        if (_settingThread || value is null || !IsStopped) return;
        _debug.SetActiveThread(value.Id);
        _ = LoadStackAsync();  // 切替先スレッドのコールスタック→変数を読み直す
    }

    partial void OnSelectedFrameChanged(DebugFrameViewModel? value)
    {
        _ = LoadFrameInspectionAsync(value);
        // フレームを選んだら、そのソース位置をエディタにプレビュー表示する（フォーカスは奪わない）。
        if (value is { HasSource: true, SourcePath: { } p })
            FramePreviewRequested?.Invoke(p, value.Line - 1);  // DAP 1始まり → エディタ 0始まり
    }

    /// <summary>コールスタックのフレームのダブルクリック：ソースへジャンプする（通常タブ＋フォーカス）。</summary>
    public void ActivateFrame(DebugFrameViewModel? frame)
    {
        if (frame is { HasSource: true, SourcePath: { } p })
            FrameActivated?.Invoke(p, frame.Line - 1);  // DAP 1始まり → エディタ 0始まり
    }

    /// <summary>選択フレームのスコープ（Locals 等）をトップ階層に並べ、ウォッチも評価し直す。</summary>
    private async Task LoadFrameInspectionAsync(DebugFrameViewModel? frame)
    {
        Variables.Clear();
        if (frame is null) return;

        var scopes = await _debug.GetScopesAsync(frame.Id);
        Func<int, string, string, Task<string?>>? setVar =
            _debug.SupportsSetVariable ? (cr, n, v) => _debug.SetVariableAsync(cr, n, v) : null;
        foreach (var s in scopes)
        {
            // スコープを「展開可能な変数ノード」として扱い、展開時に GetVariablesAsync で中身を読む。
            // スコープ自身は書き換え対象でないので containerRef=0（直下の子は scope の VR を保持側に持つ）。
            var node = new DebugVariableViewModel(
                new DebugVariable(s.Name, "", null, s.VariablesReference), 0,
                vr => _debug.GetVariablesAsync(vr), setVar);
            Variables.Add(node);
        }
        if (Variables.Count > 0) Variables[0].IsExpanded = true;  // Locals を既定で開く

        await LoadAutosAsync(frame);
        await RefreshWatchesAsync(frame.Id);
    }

    /// <summary>自動変数を読み直す。停止行とその直前行のソースから識別子を拾い、フレーム文脈で評価し、
    /// 値が取れたものだけ並べる（VS の「自動」をアダプタ非依存に近似）。</summary>
    private async Task LoadAutosAsync(DebugFrameViewModel? frame)
    {
        Autos.Clear();
        HasAutos = false;
        if (frame is not { HasSource: true, SourcePath: { } path }) return;

        string[] lines;
        try { lines = await File.ReadAllLinesAsync(path); }
        catch { return; }  // ソースが読めない（生成コード等）なら自動変数は出さない

        var idx = frame.Line - 1;  // DAP 1始まり → 配列 0始まり
        if (idx < 0 || idx >= lines.Length) return;
        var prev = idx > 0 ? lines[idx - 1] : null;

        foreach (var expr in AutosExtractor.ExtractCandidates(lines[idx], prev))
        {
            var value = await _debug.EvaluateAsync(expr, frame.Id);
            if (AutosExtractor.LooksLikeValue(value))
                Autos.Add(new WatchItemViewModel(expr) { Value = value });
        }
        HasAutos = Autos.Count > 0;
    }

    private async Task RefreshWatchesAsync(int frameId)
    {
        foreach (var w in Watches)
            w.Value = await _debug.EvaluateAsync(w.Expression, frameId);
    }

    private void ClearInspection()
    {
        CallStack.Clear();
        Variables.Clear();
        Autos.Clear();
        HasAutos = false;
        _settingThread = true;
        try { Threads.Clear(); SelectedThread = null; }
        finally { _settingThread = false; }
        foreach (var w in Watches) w.Value = "";
        Modules.Clear();
        HasModules = false;
    }

    private bool CanAddWatch() => !string.IsNullOrWhiteSpace(WatchExpression);

    [RelayCommand(CanExecute = nameof(CanAddWatch))]
    private async Task AddWatch()
    {
        var item = new WatchItemViewModel(WatchExpression.Trim());
        Watches.Add(item);
        WatchExpression = "";
        if (IsStopped && SelectedFrame is { } f)
            item.Value = await _debug.EvaluateAsync(item.Expression, f.Id);
    }

    [RelayCommand]
    private void RemoveWatch(WatchItemViewModel? item)
    {
        if (item is not null) Watches.Remove(item);
    }

    partial void OnWatchExpressionChanged(string value) => AddWatchCommand.NotifyCanExecuteChanged();

    /// <summary>イミディエイト（REPL）の入出力履歴（式とその評価結果）。</summary>
    public ObservableCollection<ImmediateEntryViewModel> ImmediateLog { get; } = new();

    /// <summary>イミディエイトの入力欄。</summary>
    [ObservableProperty] private string _immediateInput = "";

    private bool CanSubmitImmediate() => IsStopped && !string.IsNullOrWhiteSpace(ImmediateInput);

    /// <summary>イミディエイト：入力式を REPL コンテキストで評価する（副作用のある式も実行できる）。
    /// 状態を変える可能性があるので、評価後に変数/ウォッチを読み直す。</summary>
    [RelayCommand(CanExecute = nameof(CanSubmitImmediate))]
    private async Task SubmitImmediate()
    {
        var expr = ImmediateInput.Trim();
        ImmediateInput = "";
        var result = await _debug.EvaluateReplAsync(expr, SelectedFrame?.Id);
        ImmediateLog.Add(new ImmediateEntryViewModel(expr, result));
        const int max = 500;
        if (ImmediateLog.Count > max) ImmediateLog.RemoveAt(0);
        // 副作用で変数が変わり得るので検査ペインを更新する。
        if (SelectedFrame is { } f) await LoadFrameInspectionAsync(f);
    }

    [RelayCommand]
    private void ClearImmediate() => ImmediateLog.Clear();

    partial void OnImmediateInputChanged(string value) => SubmitImmediateCommand.NotifyCanExecuteChanged();

    /// <summary>ロード済みモジュール（アセンブリ）一覧。停止時に取得し、更新ボタンで読み直せる。</summary>
    public ObservableCollection<DebugModule> Modules { get; } = new();

    /// <summary>モジュールが 1 件でも取得できているか（空案内の出し分け）。</summary>
    [ObservableProperty] private bool _hasModules;

    private bool CanRefreshModules() => IsStopped;

    [RelayCommand(CanExecute = nameof(CanRefreshModules))]
    private async Task RefreshModules() => await LoadModulesAsync();

    /// <summary>モジュール一覧を取得して並べ替える（パス有りを後ろ、名前順）。</summary>
    private async Task LoadModulesAsync()
    {
        var mods = await _debug.GetModulesAsync();
        Modules.Clear();
        foreach (var m in mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            Modules.Add(m);
        HasModules = Modules.Count > 0;
    }

    /// <summary>アダプタが「次のステートメントに設定」（gotoTargets/goto）に対応しているか。
    /// エディタ右クリックメニューの出し分けに使う（未対応なら項目を出さない）。</summary>
    public bool SupportsSetNextStatement => _debug.SupportsSetNextStatement;

    /// <summary>次に実行する文をエディタのカーソル行（0 始まり）へ移動する。成功したら実行行ハイライトと
    /// コールスタック/変数を更新する。</summary>
    public async Task SetNextStatementAsync(string sourcePath, int line0)
    {
        if (!IsStopped) return;
        var ok = await _debug.SetNextStatementAsync(sourcePath, line0 + 1);  // エディタ0始まり → DAP1始まり
        if (ok)
        {
            ExecutionLineChanged?.Invoke(sourcePath, line0);
            await LoadStackAsync();
        }
    }

    /// <summary>カーソル行（0 始まり）まで実行する（一時ブレークポイントを置いて続行）。停止中のみ有効。</summary>
    public Task RunToCursorAsync(string sourcePath, int line0)
        => IsStopped
            ? _debug.RunToCursorAsync(sourcePath, line0 + 1, CancellationToken.None)  // エディタ0始まり → DAP1始まり
            : Task.CompletedTask;

    /// <summary>アダプタが「特定の関数にステップ イン」（stepInTargets）に対応しているか。
    /// エディタ右クリックメニューの出し分けに使う（未対応なら項目を出さない）。</summary>
    public bool SupportsStepInTargets => _debug.SupportsStepInTargets;

    /// <summary>停止行のステップ イン候補（先頭フレーム文脈）を取得する。停止していなければ空。</summary>
    public Task<IReadOnlyList<DebugStepInTarget>> GetStepInTargetsAsync()
        => IsStopped && SelectedFrame is { } f
            ? _debug.GetStepInTargetsAsync(f.Id)
            : Task.FromResult((IReadOnlyList<DebugStepInTarget>)Array.Empty<DebugStepInTarget>());

    /// <summary>指定の候補へステップ インする。</summary>
    public Task StepIntoTargetAsync(DebugStepInTarget target) => _debug.StepInTargetAsync(target.Id);

    /// <summary>あるファイルのカーソル行（0 始まり）のブレークポイントを返す（無ければ null）。ガターからの条件編集用。</summary>
    public BreakpointViewModel? FindBreakpoint(string sourcePath, int line0)
    {
        var path = Path.GetFullPath(sourcePath);
        return _breakpoints.TryGetValue(path, out var list) ? list.FirstOrDefault(b => b.Line0 == line0) : null;
    }

    /// <summary>カーソル行（0 始まり）にブレークポイントを取得（無ければ作成して）返す。ガターから条件を編集する際に、
    /// その行へまだブレークポイントが無くても置けるようにする。作成時はエディタのガターも同期する。</summary>
    public BreakpointViewModel EnsureBreakpoint(string sourcePath, int line0)
    {
        var path = Path.GetFullPath(sourcePath);
        if (!_breakpoints.TryGetValue(path, out var list))
            _breakpoints[path] = list = new List<BreakpointViewModel>();
        var bp = list.FirstOrDefault(b => b.Line0 == line0);
        if (bp is null)
        {
            bp = NewBreakpoint(path, line0);
            list.Add(bp);
            Breakpoints.Add(bp);
            PushBreakpoints(path);
            RefreshBreakpointFlags();
            BreakpointsRefreshed?.Invoke(path);  // エディタのガターへ反映
        }
        return bp;
    }

    /// <summary>パネルが開かれたときにアダプタ導入状況を取り直す（導入直後でも反映されるように）。</summary>
    public void Refresh() => IsAdapterMissing = !_debug.IsAdapterAvailable;

    private bool CanStart() => !IsBusy && !IsTaskRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        Refresh();
        if (IsAdapterMissing)
        {
            StatusMessage = "アダプタ未導入";
            Append(DebugOutputCategory.Important,
                $"デバッグアダプタ {DebugAdapterCatalog.Netcoredbg.Executable} が見つかりません。下のバーから導入できます。");
            return;
        }

        var program = await ResolveProgramAsync();
        if (program is null) return;

        _lastAttachProcess = null;  // この起動は launch（再起動で再 launch する）
        _cts = new CancellationTokenSource();
        try
        {
            await _debug.StartAsync(
                new DebugLaunchConfig(program, Path.GetDirectoryName(program),
                    Args: DebugLaunchArgs.ParseArgs(LaunchArgs),
                    Environment: DebugLaunchArgs.ParseEnv(LaunchEnv),
                    JustMyCode: JustMyCode),
                _cts.Token);
        }
        catch (OperationCanceledException) { /* 停止操作 */ }
        catch (Exception ex)
        {
            Append(DebugOutputCategory.Important, $"デバッグ起動でエラー: {ex.Message}");
        }
    }

    private bool CanStop() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        _cts?.Cancel();
        await _debug.StopAsync();
    }

    private bool CanRunTask() => !IsBusy && !IsTaskRunning;

    /// <summary>ワークスペースをビルドする（デバッグ起動とは独立した手動ビルド）。
    /// 対象は .sln 優先、無ければ最初の .csproj。出力はコンソールへ、結果はステータスへ。</summary>
    [RelayCommand(CanExecute = nameof(CanRunTask))]
    private async Task BuildTarget()
    {
        var target = FindBuildTarget();
        if (target is null) return;

        IsTaskRunning = true;
        try
        {
            StatusMessage = "ビルド中…";
            Append(DebugOutputCategory.Important, $"ビルド: {Path.GetFileName(target)}");
            var result = await _terminal.RunCommandAsync(
                $"dotnet build \"{target}\" -c Debug --nologo", CancellationToken.None);
            WriteConsole(result.Output);
            StatusMessage = result.Success ? "ビルド成功" : $"ビルド失敗（{result.ExitCode}）";
            Append(DebugOutputCategory.Important,
                result.Success ? "ビルドに成功しました。" : $"ビルドに失敗しました（終了コード {result.ExitCode}）。");
        }
        finally { IsTaskRunning = false; }
    }

    /// <summary>テストタブが表示されたときの保険的な収集（まだ一覧が無ければバックグラウンド収集を起動する）。
    /// 通常は <see cref="OnWorkspaceRootChanged"/> とソース監視で自動収集されるため、ここは初期化漏れ対策。</summary>
    public void EnsureTestsDiscovered()
    {
        if (Tests.Count == 0 && !IsDiscoveringTests) _ = DiscoverTestsAsync();
    }

    /// <summary>ワークスペースが変わったら監視を張り替え、すぐ収集し直す。</summary>
    private void OnWorkspaceRootChanged(object? sender, string? root)
        => _dispatcher.InvokeAsync(() => { SetupTestWatcher(root); _ = DiscoverTestsAsync(); });

    /// <summary>ソース走査でテスト一覧を収集する（ビルドを伴わない・バックグラウンド）。探索中に来た要求は
    /// 1 回にまとめて末尾でもう一度回す（編集中の連続変更で重複起動しない）。</summary>
    private async Task DiscoverTestsAsync()
    {
        var root = _workspace.RootPath;
        if (root is null) return;
        if (IsDiscoveringTests) { _rediscoverRequested = true; return; }

        IsDiscoveringTests = true;
        try
        {
            do
            {
                _rediscoverRequested = false;
                IReadOnlyList<DiscoveredTest> found;
                try { found = await Task.Run(() => _testDiscovery.Discover(root)); }
                catch { found = Array.Empty<DiscoveredTest>(); }
                // 走査の resume が UI 以外で来ても、コレクション更新は必ず UI スレッドで行う。
                await _dispatcher.InvokeAsync(() => ApplyDiscovered(found));
            } while (_rediscoverRequested);
        }
        finally { IsDiscoveringTests = false; }
    }

    /// <summary>収集結果を既存の一覧へマージする（クリアしない）。新規は追加、消えた未実行テストは除去。
    /// 既に実行結果を持つ行は探索に出てこなくても残す（パーサが拾えない種別・直前の実行結果を消さない）。</summary>
    private void ApplyDiscovered(IReadOnlyList<DiscoveredTest> found)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var existing = new Dictionary<string, TestItemViewModel>(StringComparer.Ordinal);
        foreach (var t in Tests) existing[t.FullyQualifiedName] = t;

        foreach (var d in found)
        {
            keep.Add(d.FullyQualifiedName);
            if (existing.TryGetValue(d.FullyQualifiedName, out var item))
                item.IsParameterized = d.IsParameterized;
            else
                Tests.Add(new TestItemViewModel(d.FullyQualifiedName) { IsParameterized = d.IsParameterized });
        }

        // 探索に現れなくなった「未実行」の行だけ掃除する（結果を持つ行は残す）。
        for (var i = Tests.Count - 1; i >= 0; i--)
        {
            var t = Tests[i];
            if (keep.Contains(t.FullyQualifiedName)) continue;
            if (t.Status != TestStatus.NotRun) continue;
            Tests.RemoveAt(i);
        }

        SyncTree();
        RecomputeSummary();
    }

    /// <summary>ソース監視を <paramref name="root"/> に張り替える。<c>*.cs</c> の追加/削除/変更を遅延付きで拾い、
    /// 自動再収集する。監視できない（権限/パス）場合は自動更新なしで動く（手動契機の <see cref="EnsureTestsDiscovered"/> は残る）。</summary>
    private void SetupTestWatcher(string? root)
    {
        _testWatcher?.Dispose();
        _testWatcher = null;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        try
        {
            var w = new FileSystemWatcher(root, "*.cs")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
            };
            w.Changed += OnTestSourceChanged;
            w.Created += OnTestSourceChanged;
            w.Deleted += OnTestSourceChanged;
            w.Renamed += OnTestSourceChanged;
            w.Error += (_, _) => _dispatcher.InvokeAsync(ScheduleRediscover);
            w.EnableRaisingEvents = true;
            _testWatcher = w;
        }
        catch { /* 監視不可。自動更新なしでも探索自体は動く */ }
    }

    private void OnTestSourceChanged(object sender, FileSystemEventArgs e)
    {
        var sep = Path.DirectorySeparatorChar;
        var p = e.FullPath;
        // ビルド成果物配下の .cs（生成物・コピー）はテスト集合に影響しないので無視（ビルド中の連続通知も抑える）。
        if (p.Contains($"{sep}bin{sep}") || p.Contains($"{sep}obj{sep}") || p.Contains($"{sep}artifacts{sep}"))
            return;
        _dispatcher.InvokeAsync(ScheduleRediscover);
    }

    private void ScheduleRediscover()
    {
        _discoverDebounce ??= new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(1200),
        };
        if (!_isDebounceWired)
        {
            _discoverDebounce.Tick += (_, _) => { _discoverDebounce!.Stop(); _ = DiscoverTestsAsync(); };
            _isDebounceWired = true;
        }
        _discoverDebounce.Stop();
        _discoverDebounce.Start();
    }

    private bool _isDebounceWired;

    public void Dispose()
    {
        _workspace.RootChanged -= OnWorkspaceRootChanged;
        _testWatcher?.Dispose();
        _testWatcher = null;
        _discoverDebounce?.Stop();
    }

    /// <summary>ワークスペースの全テストを実行する（<c>dotnet test</c> ＋ TRX ロガー）。結果を各行へ反映する。
    /// 失敗テストはダブルクリックで該当行へジャンプ、▶ で個別実行できる。</summary>
    [RelayCommand(CanExecute = nameof(CanRunTask))]
    private async Task Test()
    {
        var target = FindBuildTarget();
        if (target is null) return;

        IsTaskRunning = true;
        try
        {
            StatusMessage = "テスト実行中…";
            foreach (var t in Tests) t.SetRunning();
            var trx = await RunDotnetTestAsync(target, null, $"テスト: {Path.GetFileName(target)}");
            if (trx is not null) ApplyTrx(trx);
            // 実行されなかった探索済み行（フィルタ外など）は未実行へ戻す。
            foreach (var t in Tests) if (t.Status == TestStatus.Running) t.ResetStatus();
            SyncTree();
            RecomputeSummary();

            var failed = Tests.Count(t => t.Status == TestStatus.Failed);
            StatusMessage = trx is null ? "テスト結果を取得できませんでした"
                : failed == 0 ? "テスト成功" : $"テスト失敗（{failed} 件）";
        }
        finally { IsTaskRunning = false; }
    }

    /// <summary>1 件のテストだけ実行する（<c>--filter "FullyQualifiedName=..."</c>）。テオリは同メソッドの全ケースが対象。</summary>
    [RelayCommand(CanExecute = nameof(CanRunTask))]
    private async Task RunSingleTest(TestItemViewModel? item)
    {
        if (item is null) return;
        var target = FindBuildTarget();
        if (target is null) return;

        IsTaskRunning = true;
        try
        {
            StatusMessage = $"テスト実行中… {item.DisplayName}";
            item.SetRunning();
            UpdateAggregates();
            var trx = await RunDotnetTestAsync(target, $"FullyQualifiedName={item.FilterExpression}", $"テスト: {item.DisplayName}");
            if (trx is not null) ApplyTrx(trx);
            if (item.Status == TestStatus.Running) item.ResetStatus();  // 何も突き合わなければ戻す
            SyncTree();
            RecomputeSummary();

            StatusMessage = item.Status switch
            {
                TestStatus.Failed => "テスト失敗",
                TestStatus.Passed => "テスト成功",
                _ => trx is null ? "テスト結果を取得できませんでした" : "テスト完了",
            };
        }
        finally { IsTaskRunning = false; }
    }

    /// <summary>クラスグループ内のテストをまとめて実行する（<c>--filter "FullyQualifiedName~Namespace.Class."</c>）。</summary>
    [RelayCommand(CanExecute = nameof(CanRunTask))]
    private async Task RunGroup(TestGroupViewModel? group)
    {
        if (group is null) return;
        var target = FindBuildTarget();
        if (target is null) return;

        IsTaskRunning = true;
        try
        {
            StatusMessage = $"テスト実行中… {group.Name}";
            foreach (var t in group.Tests) t.SetRunning();
            group.RecomputeAggregate();
            var trx = await RunDotnetTestAsync(target, $"FullyQualifiedName~{group.Key}.", $"テスト: {group.Name}");
            if (trx is not null) ApplyTrx(trx);
            foreach (var t in group.Tests) if (t.Status == TestStatus.Running) t.ResetStatus();
            SyncTree();
            RecomputeSummary();

            var failed = group.Tests.Count(t => t.Status == TestStatus.Failed);
            StatusMessage = trx is null ? "テスト結果を取得できませんでした"
                : failed == 0 ? "テスト成功" : $"テスト失敗（{failed} 件）";
        }
        finally { IsTaskRunning = false; }
    }

    partial void OnIsTaskRunningChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
        AttachCommand.NotifyCanExecuteChanged();
        BuildTargetCommand.NotifyCanExecuteChanged();
        TestCommand.NotifyCanExecuteChanged();
        RunSingleTestCommand.NotifyCanExecuteChanged();
        RunGroupCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsDiscoveringTestsChanged(bool value) => OnPropertyChanged(nameof(TestEmptyHint));

    /// <summary>フラットな <see cref="Tests"/> をクラス単位のツリーへ再構築する。展開状態は <see cref="TestGroupViewModel.Key"/>
    /// で引き継ぐ（葉は同一インスタンスを使い回すのでステータスのバインドは保たれる）。</summary>
    private void SyncTree()
    {
        var expanded = TestTree.ToDictionary(g => g.Key, g => g.IsExpanded);
        TestTree.Clear();

        // 状態トグルやテキスト検索で絞り込み中は、一致が埋もれないよう全グループを開く。
        var filtering = !string.IsNullOrEmpty(TestFilter?.Trim())
            || !(ShowPassed && ShowFailed && ShowNotRun);

        foreach (var g in Tests.GroupBy(t => t.ClassName).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var visible = g.Where(MatchesFilter).OrderBy(t => t.DisplayName, StringComparer.Ordinal).ToList();
            if (visible.Count == 0) continue;  // フィルタで全部隠れたクラスは出さない

            var name = g.Key.Length == 0 ? "(その他)"
                : g.Key[(g.Key.LastIndexOf('.') + 1)..];
            var node = new TestGroupViewModel(g.Key, name);
            foreach (var t in visible) node.Tests.Add(t);
            node.IsExpanded = filtering || (expanded.TryGetValue(g.Key, out var e) && e);
            node.RecomputeAggregate();
            TestTree.Add(node);
        }

        HasVisibleTests = TestTree.Count > 0;
    }

    /// <summary>1 件のテストがフィルタ（状態トグル＋テキスト検索）に合致するか。スキップは「未実施」側、
    /// 実行中は常に表示する。</summary>
    private bool MatchesFilter(TestItemViewModel t)
    {
        var statusOk = t.Status switch
        {
            TestStatus.Passed => ShowPassed,
            TestStatus.Failed => ShowFailed,
            TestStatus.NotRun => ShowNotRun,
            TestStatus.Skipped => ShowNotRun,
            _ => true,  // Running 等の一時状態は隠さない
        };
        if (!statusOk) return false;

        var f = TestFilter?.Trim();
        return string.IsNullOrEmpty(f)
            || t.FullyQualifiedName.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>葉のステータスだけ変えたとき（実行開始時など）にグループの集計を更新する。</summary>
    private void UpdateAggregates()
    {
        foreach (var g in TestTree) g.RecomputeAggregate();
    }

    /// <summary>失敗テストのダブルクリック：スタックトレースから拾った位置へジャンプする（通常タブ＋フォーカス）。</summary>
    public void NavigateToTestSource(TestItemViewModel? t)
    {
        if (t is { HasSource: true, SourcePath: { } p })
            FrameActivated?.Invoke(p, t.Line - 1);  // 1始まり → エディタ 0始まり
    }

    /// <summary>ビルド/テスト対象を解決する。ワークスペース直下の .sln を優先し、無ければ最初の .csproj。</summary>
    private string? FindBuildTarget()
    {
        var root = _workspace.RootPath;
        if (root is null)
        {
            Append(DebugOutputCategory.Important, "ワークスペースが開かれていません。");
            return null;
        }
        try
        {
            var sln = Directory.GetFiles(root, "*.sln", SearchOption.TopDirectoryOnly);
            if (sln.Length > 0) return sln[0];
        }
        catch { /* 列挙失敗時は csproj へフォールバック */ }

        var csproj = FindProject(root);
        if (csproj is null)
            Append(DebugOutputCategory.Important, "ワークスペースに .sln/.csproj が見つかりません。");
        return csproj;
    }

    /// <summary>TRX の出力先ディレクトリ（毎回上書き）。<c>%TEMP%/Loomo/test-results</c>。</summary>
    private static readonly string TestResultsDir =
        Path.Combine(Path.GetTempPath(), "Loomo", "test-results");

    /// <summary>テストのビルド出力を既定 <c>bin</c> の外（各プロジェクト配下の <c>artifacts/loomo-test</c>）へ逃がす
    /// MSBuild 引数。<b>起動中の Loomo 自身が既定 bin の .exe/.dll をロックし、<c>dotnet test</c> のビルド（参照する
    /// App を含む）が失敗する</b>のを防ぐ。<c>BaseOutputPath</c> は相対なのでプロジェクトごとに分かれ衝突しない。
    /// スラッシュ表記は末尾バックスラッシュによる MSBuild のクォート問題を避けるため。</summary>
    private const string TestBuildRedirect = "/p:BaseOutputPath=artifacts/loomo-test/";

    /// <summary><c>dotnet test</c> を TRX ロガー付きで実行する。<paramref name="filterExpr"/> が非 null なら
    /// <c>--filter "&lt;filterExpr&gt;"</c> を付ける（例 <c>FullyQualifiedName=...</c> / <c>FullyQualifiedName~...</c>）。
    /// 出力はコンソールへ流し、生成された TRX のパスを返す（生成されなければ null）。CLI 言語は英語に固定。</summary>
    private async Task<string?> RunDotnetTestAsync(string target, string? filterExpr, string label)
    {
        string trx;
        try
        {
            Directory.CreateDirectory(TestResultsDir);
            trx = Path.Combine(TestResultsDir, "loomo.trx");
            if (File.Exists(trx)) File.Delete(trx);  // 前回分を残さない
        }
        catch (Exception ex)
        {
            Append(DebugOutputCategory.Important, $"テスト結果フォルダを準備できません: {ex.Message}");
            return null;
        }

        var filterArg = filterExpr is null ? "" : $" --filter \"{filterExpr}\"";
        Append(DebugOutputCategory.Important, label);
        var result = await _terminal.RunCommandAsync(
            $"$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test \"{target}\"{filterArg} --nologo {TestBuildRedirect} " +
            $"--logger \"trx;LogFileName=loomo.trx\" --results-directory \"{TestResultsDir}\"",
            CancellationToken.None);
        WriteConsole(result.Output);
        return File.Exists(trx) ? trx : null;
    }

    /// <summary>TRX（VSTest 形式 XML）を読み、各 <c>UnitTestResult</c> を名前で突き合わせて行のステータス・
    /// 失敗メッセージ・ソース位置（スタックトレースの <c>in &lt;path&gt;:line N</c>）を更新する。
    /// 一覧に無いテストは追加する（探索せず実行した場合に対応）。</summary>
    private void ApplyTrx(string trxPath)
    {
        XDocument doc;
        try { doc = XDocument.Load(trxPath); }
        catch (Exception ex)
        {
            Append(DebugOutputCategory.Important, $"テスト結果(TRX)を読めません: {ex.Message}");
            return;
        }

        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var locRe = new Regex(@"\sin\s+(.+?):line\s+(\d+)");

        foreach (var r in doc.Descendants(ns + "UnitTestResult"))
        {
            var name = (string?)r.Attribute("testName");
            if (string.IsNullOrEmpty(name)) continue;

            var status = ((string?)r.Attribute("outcome")) switch
            {
                "Passed" => TestStatus.Passed,
                "Failed" => TestStatus.Failed,
                _ => TestStatus.Skipped,  // NotExecuted など
            };

            string? msg = null, path = null;
            var line1 = 0;
            var err = r.Element(ns + "Output")?.Element(ns + "ErrorInfo");
            if (err is not null)
            {
                msg = ((string?)err.Element(ns + "Message"))?.Trim();
                if (msg is not null)
                {
                    var nl = msg.IndexOf('\n');  // 失敗メッセージは先頭行だけ一覧に出す
                    if (nl >= 0) msg = msg[..nl].Trim();
                }
                var stack = (string?)err.Element(ns + "StackTrace");
                if (stack is not null)
                {
                    var lm = locRe.Match(stack);
                    if (lm.Success) { path = lm.Groups[1].Value.Trim(); line1 = int.Parse(lm.Groups[2].Value); }
                }
            }

            // まず完全名で突き合わせる。テオリ等のケース（"FQN(args)"）は、引数を落とした名前で
            // メソッド単位の行へ集約する（ソース走査では引数なしのメソッドしか持たないため）。
            var item = Tests.FirstOrDefault(t => string.Equals(t.FullyQualifiedName, name, StringComparison.Ordinal));
            var isCase = false;
            if (item is null)
            {
                var paren = name.IndexOf('(');
                if (paren > 0)
                {
                    var baseName = name[..paren];
                    item = Tests.FirstOrDefault(t => string.Equals(t.FilterExpression, baseName, StringComparison.Ordinal));
                    isCase = item is not null;
                }
            }
            if (item is null) { item = new TestItemViewModel(name); Tests.Add(item); }

            if (isCase) item.ApplyCaseResult(status, msg, path, line1);
            else item.Update(status, msg, path, line1);
        }
    }

    /// <summary>一覧の各行ステータスから集計（成功/失敗/スキップ/合計）を作り直し、案内の出し分けも更新する。</summary>
    private void RecomputeSummary()
    {
        HasTestResults = Tests.Count > 0;
        if (!HasTestResults) { TestSummary = ""; return; }

        var passed = Tests.Count(t => t.Status == TestStatus.Passed);
        var failed = Tests.Count(t => t.Status == TestStatus.Failed);
        var skipped = Tests.Count(t => t.Status == TestStatus.Skipped);
        TestSummary = $"成功 {passed} / 失敗 {failed} / スキップ {skipped} / 合計 {Tests.Count}";
    }

    /// <summary>複数行のコマンド出力を 1 行ずつコンソールへ流す（末尾の CR を落とし、空行は捨てる）。</summary>
    private void WriteConsole(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (t.Length > 0) Append(DebugOutputCategory.Console, t);
        }
    }

    /// <summary>アタッチパネルの開閉。開くときに（未取得なら）プロセス一覧を読み込む。</summary>
    [RelayCommand]
    private async Task ToggleAttach()
    {
        ShowAttach = !ShowAttach;
        if (ShowAttach && _allProcesses.Count == 0)
            await RefreshProcessesAsync();
    }

    private bool CanAttach() => !IsBusy && !IsTaskRunning && SelectedProcess is not null;

    /// <summary>選択中プロセスにアタッチする。停止してもそのプロセスは終了しない（デタッチのみ）。</summary>
    [RelayCommand(CanExecute = nameof(CanAttach))]
    private async Task Attach()
    {
        if (SelectedProcess is { } proc) await AttachToAsync(proc);
    }

    /// <summary>指定プロセスにアタッチする（コマンド本体と再起動の共通処理）。</summary>
    private async Task AttachToAsync(DebugProcessViewModel proc)
    {
        Refresh();
        if (IsAdapterMissing)
        {
            StatusMessage = "アダプタ未導入";
            Append(DebugOutputCategory.Important,
                $"デバッグアダプタ {DebugAdapterCatalog.Netcoredbg.Executable} が見つかりません。下のバーから導入できます。");
            return;
        }

        ShowAttach = false;
        _lastAttachProcess = proc;  // 再起動で再アタッチする対象
        _cts = new CancellationTokenSource();
        try
        {
            await _debug.AttachAsync(new DebugAttachConfig(proc.Pid, proc.Name), _cts.Token);
        }
        catch (OperationCanceledException) { /* 停止操作 */ }
        catch (Exception ex)
        {
            Append(DebugOutputCategory.Important, $"アタッチでエラー: {ex.Message}");
        }
    }

    private bool CanRefreshProcesses() => !IsEnumeratingProcesses;

    /// <summary>実行中プロセスを列挙し直す。判定（coreclr ロード検出）が重いのでバックグラウンドで実行する。</summary>
    [RelayCommand(CanExecute = nameof(CanRefreshProcesses))]
    private async Task RefreshProcesses() => await RefreshProcessesAsync();

    private async Task RefreshProcessesAsync()
    {
        IsEnumeratingProcesses = true;
        try
        {
            _allProcesses = await Task.Run(EnumerateProcesses);
            ApplyProcessFilter();
        }
        finally { IsEnumeratingProcesses = false; }
    }

    /// <summary>全プロセスを列挙し、coreclr 等のロード有無で .NET 判定を付ける。判定不能なものは未管理扱い。</summary>
    private static List<DebugProcessViewModel> EnumerateProcesses()
    {
        var self = Environment.ProcessId;
        var list = new List<DebugProcessViewModel>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self || p.Id == 0) continue;  // 自分自身と Idle は除外
                string? title = null;
                try { title = string.IsNullOrWhiteSpace(p.MainWindowTitle) ? null : p.MainWindowTitle; }
                catch { /* タイトル取得不能 */ }
                list.Add(new DebugProcessViewModel(p.Id, p.ProcessName, title, IsManaged(p)));
            }
            catch { /* 列挙中に終了した等。スキップ */ }
            finally { p.Dispose(); }
        }
        return list
            .OrderByDescending(i => i.IsManaged)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Pid)
            .ToList();
    }

    /// <summary>プロセスに coreclr/clr がロードされているかで .NET 実行中かを推定する。
    /// アクセス権限不足・ビット不一致では例外になり判定不能（false）。netcoredbg でアタッチ可能なのも
    /// おおむね検査できるプロセスに限られるため、この best-effort で十分。</summary>
    private static bool IsManaged(Process p)
    {
        try
        {
            foreach (ProcessModule m in p.Modules)
            {
                var n = m.ModuleName;
                if (n.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("clr.dll", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("hostpolicy.dll", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { /* 判定不能 */ }
        return false;
    }

    private void ApplyProcessFilter()
    {
        IEnumerable<DebugProcessViewModel> q = _allProcesses;
        if (DotnetOnly) q = q.Where(p => p.IsManaged);
        var f = ProcessFilter?.Trim();
        if (!string.IsNullOrEmpty(f))
            q = q.Where(p =>
                p.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                p.Pid.ToString().Contains(f) ||
                (p.Title?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false));

        Processes.Clear();
        foreach (var p in q) Processes.Add(p);
    }

    partial void OnProcessFilterChanged(string value) => ApplyProcessFilter();
    partial void OnDotnetOnlyChanged(bool value) => ApplyProcessFilter();
    partial void OnSelectedProcessChanged(DebugProcessViewModel? value) => AttachCommand.NotifyCanExecuteChanged();
    partial void OnIsEnumeratingProcessesChanged(bool value) => RefreshProcessesCommand.NotifyCanExecuteChanged();

    /// <summary>促しバーの「インストール」。導入コマンドを見えるターミナルで実行する。</summary>
    [RelayCommand]
    private void InstallAdapter()
    {
        if (!string.IsNullOrWhiteSpace(AdapterInstallCommand))
            _terminal.TryRunInVisibleTerminal(AdapterInstallCommand);
    }

    /// <summary>デバッグ対象を解決する。明示指定が無ければワークスペースの .csproj を 1 つ探し、
    /// 任意でビルドしてから出力 dll を見つける。解決できなければ null（理由はコンソールへ）。</summary>
    private async Task<string?> ResolveProgramAsync()
    {
        var root = _workspace.RootPath;

        // 明示指定があればそれを優先（相対はワークスペース基準）。
        if (!string.IsNullOrWhiteSpace(TargetProgram))
        {
            var p = Path.IsPathRooted(TargetProgram) || root is null
                ? TargetProgram
                : Path.GetFullPath(Path.Combine(root, TargetProgram));
            if (File.Exists(p)) return p;
            Append(DebugOutputCategory.Important, $"指定された実行対象が見つかりません: {p}");
            return null;
        }

        if (root is null)
        {
            Append(DebugOutputCategory.Important, "ワークスペースが開かれていません。デバッグ対象を指定してください。");
            return null;
        }

        var csproj = FindProject(root);
        if (csproj is null)
        {
            Append(DebugOutputCategory.Important,
                "ワークスペースに .csproj が見つかりません。デバッグ対象（.dll/.exe）を直接指定してください。");
            return null;
        }

        if (BuildFirst && !await BuildAsync(csproj))
            return null;

        var dll = FindOutputDll(csproj);
        if (dll is null)
        {
            Append(DebugOutputCategory.Important,
                $"ビルド出力 (.dll) が見つかりません。先にビルドするか、対象を直接指定してください。");
            return null;
        }
        return dll;
    }

    /// <summary>ワークスペース直下、無ければ浅い再帰で最初の .csproj を探す。</summary>
    private static string? FindProject(string root)
    {
        try
        {
            var top = Directory.GetFiles(root, "*.csproj", SearchOption.TopDirectoryOnly);
            if (top.Length > 0) return top[0];
            return Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                .OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar))
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary><c>dotnet build</c> を実行し、出力をコンソールへ。成功（exit 0）なら true。</summary>
    private async Task<bool> BuildAsync(string csproj)
    {
        StatusMessage = "ビルド中…";
        Append(DebugOutputCategory.Important, $"ビルド: {Path.GetFileName(csproj)}");
        var result = await _terminal.RunCommandAsync($"dotnet build \"{csproj}\" -c Debug --nologo", CancellationToken.None);
        foreach (var line in result.Output.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (t.Length > 0) Append(DebugOutputCategory.Console, t);
        }
        if (!result.Success)
        {
            StatusMessage = "ビルド失敗";
            Append(DebugOutputCategory.Important, $"ビルドに失敗しました（終了コード {result.ExitCode}）。");
            return false;
        }
        return true;
    }

    /// <summary>プロジェクトの <c>bin/Debug</c> 配下から <c>&lt;projName&gt;.dll</c> を新しい順に探す。</summary>
    private static string? FindOutputDll(string csproj)
    {
        try
        {
            var projDir = Path.GetDirectoryName(csproj)!;
            var name = Path.GetFileNameWithoutExtension(csproj);
            var binDir = Path.Combine(projDir, "bin", "Debug");
            if (!Directory.Exists(binDir)) return null;
            return Directory.EnumerateFiles(binDir, name + ".dll", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private void OnOutput(object? sender, DebugOutput e)
        => _dispatcher.InvokeAsync(() => Append(e.Category, e.Text.TrimEnd('\r', '\n')));

    private void OnStateChanged(object? sender, DebugSessionState state)
        => _dispatcher.InvokeAsync(() =>
        {
            IsBusy = state is DebugSessionState.Launching or DebugSessionState.Running or DebugSessionState.Stopped;
            IsStopped = state is DebugSessionState.Stopped;
            // セッション終了（手動停止＝Idle 含む）では Exited イベントが届かないことがあるので、
            // ここで実行行ハイライトと検査ペインを確実に片付ける。
            if (state is DebugSessionState.Idle or DebugSessionState.Terminated or DebugSessionState.Failed)
            {
                ExecutionLineChanged?.Invoke(null, -1);
                ClearInspection();
            }
            ContinueCommand.NotifyCanExecuteChanged();
            StepOverCommand.NotifyCanExecuteChanged();
            StepIntoCommand.NotifyCanExecuteChanged();
            StepOutCommand.NotifyCanExecuteChanged();
            PauseCommand.NotifyCanExecuteChanged();
            RestartCommand.NotifyCanExecuteChanged();
            SubmitImmediateCommand.NotifyCanExecuteChanged();
            RefreshModulesCommand.NotifyCanExecuteChanged();
            StatusMessage = state switch
            {
                DebugSessionState.Idle => "待機中",
                DebugSessionState.Launching => "起動中…",
                DebugSessionState.Running => "実行中",
                DebugSessionState.Stopped => "停止中（ブレークポイント）",
                DebugSessionState.Terminated => "終了",
                DebugSessionState.Failed => "失敗",
                _ => StatusMessage,
            };
            StartCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            AttachCommand.NotifyCanExecuteChanged();
        });

    private void Append(DebugOutputCategory category, string text)
    {
        Output.Add(new DebugOutputLine(category, text));
        const int max = 2000;
        if (Output.Count > max) Output.RemoveAt(0);
    }
}
