using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

    private bool CanStart() => !IsBusy;

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

    [RelayCommand]
    private void Clear() => Output.Clear();

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
        });

    private void Append(DebugOutputCategory category, string text)
    {
        Output.Add(new DebugOutputLine(category, text));
        const int max = 2000;
        if (Output.Count > max) Output.RemoveAt(0);
    }
}
