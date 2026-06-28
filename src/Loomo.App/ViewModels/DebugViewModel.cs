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
public sealed partial class DebugViewModel : ObservableObject
{
    private readonly IDebugService _debug;
    private readonly IWorkspaceService _workspace;
    private readonly ITerminalService _terminal;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _cts;

    /// <summary>デバッグ対象（<c>*.dll</c>/<c>*.exe</c>）の明示指定。空ならワークスペースから自動検出する。</summary>
    [ObservableProperty] private string _targetProgram = "";

    /// <summary>起動前に <c>dotnet build</c> を実行するか。</summary>
    [ObservableProperty] private bool _buildFirst = true;

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

    /// <summary>直近のテストで失敗したテスト一覧（ダブルクリックで該当行へジャンプ）。</summary>
    public ObservableCollection<TestResultViewModel> TestFailures { get; } = new();

    /// <summary>ソースパス（絶対）→ ブレークポイント行（0 始まり）。エディタ表示と一致させる。</summary>
    private readonly Dictionary<string, SortedSet<int>> _breakpoints = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>ウォッチ式。</summary>
    public ObservableCollection<WatchItemViewModel> Watches { get; } = new();

    /// <summary>ウォッチ追加欄。</summary>
    [ObservableProperty] private string _watchExpression = "";

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

    public DebugViewModel(IDebugService debug, IWorkspaceService workspace, ITerminalService terminal)
    {
        _debug = debug;
        _workspace = workspace;
        _terminal = terminal;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _debug.Output += OnOutput;
        _debug.StateChanged += OnStateChanged;
        _debug.Stopped += OnStopped;
        _debug.Continued += OnContinued;
        _debug.Exited += OnDebugExited;

        IsAdapterMissing = !_debug.IsAdapterAvailable;
    }

    /// <summary>あるファイルのブレークポイントをトグルする（行は 0 始まり）。新しい行集合を返す。
    /// デバッグアダプタへも（1 始まりへ変換して）反映する。</summary>
    public IReadOnlyList<int> ToggleBreakpoint(string sourcePath, int line0)
    {
        var path = Path.GetFullPath(sourcePath);
        if (!_breakpoints.TryGetValue(path, out var set))
            _breakpoints[path] = set = new SortedSet<int>();
        if (!set.Remove(line0)) set.Add(line0);
        if (set.Count == 0) _breakpoints.Remove(path);

        var lines0 = set.ToList();
        _ = _debug.SetBreakpointsAsync(path, lines0.Select(l => l + 1).ToList(), CancellationToken.None);
        return lines0;
    }

    /// <summary>あるファイルのブレークポイント行（0 始まり）を返す（エディタ同期用）。</summary>
    public IReadOnlyList<int> GetBreakpoints(string sourcePath)
        => _breakpoints.TryGetValue(Path.GetFullPath(sourcePath), out var set) ? set.ToList() : Array.Empty<int>();

    private bool CanStep() => IsStopped;

    [RelayCommand(CanExecute = nameof(CanStep))]
    private async Task Continue() => await _debug.ContinueAsync();

    [RelayCommand(CanExecute = nameof(CanStep))]
    private async Task StepOver() => await _debug.StepOverAsync();

    [RelayCommand(CanExecute = nameof(CanStep))]
    private async Task StepInto() => await _debug.StepInAsync();

    [RelayCommand(CanExecute = nameof(CanStep))]
    private async Task StepOut() => await _debug.StepOutAsync();

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
            await LoadStackAsync();
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
        foreach (var s in scopes)
        {
            // スコープを「展開可能な変数ノード」として扱い、展開時に GetVariablesAsync で中身を読む。
            var node = new DebugVariableViewModel(
                new DebugVariable(s.Name, "", null, s.VariablesReference),
                vr => _debug.GetVariablesAsync(vr));
            Variables.Add(node);
        }
        if (Variables.Count > 0) Variables[0].IsExpanded = true;  // Locals を既定で開く

        await RefreshWatchesAsync(frame.Id);
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
        foreach (var w in Watches) w.Value = "";
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

        _cts = new CancellationTokenSource();
        try
        {
            await _debug.StartAsync(
                new DebugLaunchConfig(program, Path.GetDirectoryName(program)),
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

    /// <summary>ワークスペースのテストを実行する（<c>dotnet test</c>）。出力はコンソールへ流し、
    /// 集計と失敗テストの一覧を「テスト」タブへ反映する。失敗テストはダブルクリックで該当行へジャンプ。</summary>
    [RelayCommand(CanExecute = nameof(CanRunTask))]
    private async Task Test()
    {
        var target = FindBuildTarget();
        if (target is null) return;

        IsTaskRunning = true;
        try
        {
            StatusMessage = "テスト実行中…";
            TestFailures.Clear();
            TestSummary = "";
            HasTestResults = false;
            Append(DebugOutputCategory.Important, $"テスト: {Path.GetFileName(target)}");
            // 出力の集計行・失敗行を安定して解析するため、CLI 表示言語を英語に固定する。
            var result = await _terminal.RunCommandAsync(
                $"$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test \"{target}\" --nologo", CancellationToken.None);
            WriteConsole(result.Output);
            ParseTestResults(result.Output);
            StatusMessage = result.Success ? "テスト成功" : "テスト失敗";
        }
        finally { IsTaskRunning = false; }
    }

    partial void OnIsTaskRunningChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
        AttachCommand.NotifyCanExecuteChanged();
        BuildTargetCommand.NotifyCanExecuteChanged();
        TestCommand.NotifyCanExecuteChanged();
    }

    /// <summary>失敗テストのダブルクリック：スタックトレースから拾った位置へジャンプする（通常タブ＋フォーカス）。</summary>
    public void NavigateToTestSource(TestResultViewModel? t)
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

    /// <summary><c>dotnet test</c> の出力から集計（成功/失敗/スキップ/合計）と失敗テスト（名前・メッセージ・
    /// ソース位置）を抽出して <see cref="TestFailures"/>/<see cref="TestSummary"/> へ反映する。
    /// 複数テストプロジェクトの集計行は合算する。CLI 言語は英語に固定済み（<see cref="Test"/>）。</summary>
    private void ParseTestResults(string output)
    {
        var summaryRe = new Regex(@"Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)");
        var failedRe = new Regex(@"^\s*Failed\s+(.+?)\s+\[\d");
        var locRe = new Regex(@"\sin\s+(.+?):line\s+(\d+)");

        int failed = 0, passed = 0, skipped = 0, total = 0;
        var sawSummary = false;

        string? name = null, msg = null, path = null;
        var line1 = 0;
        var grabMsg = false;

        void Flush()
        {
            if (name is not null) TestFailures.Add(new TestResultViewModel(name, msg, path, line1));
            name = null; msg = null; path = null; line1 = 0; grabMsg = false;
        }

        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            var sm = summaryRe.Match(raw);
            if (sm.Success)
            {
                failed += int.Parse(sm.Groups[1].Value);
                passed += int.Parse(sm.Groups[2].Value);
                skipped += int.Parse(sm.Groups[3].Value);
                total += int.Parse(sm.Groups[4].Value);
                sawSummary = true;
                Flush();
                continue;
            }

            var fm = failedRe.Match(raw);
            if (fm.Success)
            {
                Flush();
                name = fm.Groups[1].Value.Trim();
                continue;
            }

            if (name is null) continue;  // 失敗ブロックの中だけ詳細を拾う

            if (path is null)
            {
                var lm = locRe.Match(raw);
                if (lm.Success) { path = lm.Groups[1].Value.Trim(); line1 = int.Parse(lm.Groups[2].Value); }
            }

            var t = raw.Trim();
            if (t.StartsWith("Error Message:")) { grabMsg = true; continue; }
            if (grabMsg && msg is null && t.Length > 0 && !t.StartsWith("Stack Trace:")) { msg = t; grabMsg = false; }
        }
        Flush();

        HasTestResults = sawSummary || TestFailures.Count > 0;
        TestSummary = sawSummary
            ? $"成功 {passed} / 失敗 {failed} / スキップ {skipped} / 合計 {total}"
            : TestFailures.Count > 0 ? $"失敗 {TestFailures.Count} 件" : "結果を取得できませんでした";
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
        var proc = SelectedProcess;
        if (proc is null) return;

        Refresh();
        if (IsAdapterMissing)
        {
            StatusMessage = "アダプタ未導入";
            Append(DebugOutputCategory.Important,
                $"デバッグアダプタ {DebugAdapterCatalog.Netcoredbg.Executable} が見つかりません。下のバーから導入できます。");
            return;
        }

        ShowAttach = false;
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
