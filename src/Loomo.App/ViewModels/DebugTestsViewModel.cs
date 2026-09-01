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
using sk0ya.Loomo.CSharp.Build;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.CSharp.Testing;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>テストエクスプローラのサブ ViewModel。ソース走査でテストを自動収集し、<c>dotnet test</c>（TRX）の結果で
/// 各行のステータスを更新する。全実行/クラス実行/個別実行と、状態・名前による絞り込みを持つ。</summary>
public sealed partial class DebugTestsViewModel : ObservableObject, ITestExplorer, IDisposable
{
    /// <summary>共有テストビューでC#のtesthostデバッグ操作を表示する。</summary>
    public bool IsTestDebugSupported => true;

    private readonly IWorkspaceService _workspace;
    private readonly ITerminalService _terminal;
    private readonly ITestDiscoveryService _testDiscovery;
    private readonly IDebugSession _session;
    private readonly ISolutionModelService? _solutionModel;
    private readonly Dispatcher _dispatcher;
    private readonly DebugViewModel _manager;
    // ワークスペースフォルダーごとに1つ（複数フォルダー時は全フォルダーを監視する）。
    private readonly List<TestSourceWatcher> _watchers = new();
    private CSharpTestDebugProcess? _testDebugProcess;
    private DebugSessionViewModel? _testDebugSession;

    /// <summary>探索中に来た再収集要求（探索完了後にもう一度走らせる）。</summary>
    private bool _rediscoverRequested;

    /// <summary>直近のテスト実行の集計（成功/失敗/スキップ/合計）。テストタブのヘッダに表示する。</summary>
    [ObservableProperty] private string _testSummary = "";
    /// <summary>直近のcoverlet集計。カバレッジ解析・ファイル一覧はC#機能DLLが持ち、ここは要約表示だけを持つ。</summary>
    [ObservableProperty] private string _coverageSummary = "";
    /// <summary>直近のcoverage行情報。エディタの汎用markerへ渡すためのC#側の正本。</summary>
    public IReadOnlyList<CoverageFileSummary> CoverageFiles { get; private set; } = Array.Empty<CoverageFileSummary>();
    [ObservableProperty] private bool _isCoverageDetailsVisible;
    /// <summary>テスト結果が 1 度でも得られたか（テストタブの案内文の出し分けに使う）。</summary>
    [ObservableProperty] private bool _hasTestResults;
    /// <summary>直近の一覧にある失敗テスト数。失敗再実行コマンドの有効状態にも使う。</summary>
    [ObservableProperty] private int _failedCount;
    /// <summary>バックグラウンドのテスト収集を実行中か（収集中インジケータと空状態の案内文に使う）。</summary>
    [ObservableProperty] private bool _isDiscoveringTests;

    /// <summary>テストがまだ無いときの案内文（収集中かどうかで出し分ける）。</summary>
    public string TestEmptyHint => IsDiscoveringTests
        ? "テストを収集しています…"
        : "テストが見つかりませんでした（ソース変更で自動収集します）。";

    /// <summary>絞り込み：成功／失敗／未実施（探索だけ・スキップ）のテストを表示するか。</summary>
    [ObservableProperty] private bool _showPassed = true;
    [ObservableProperty] private bool _showFailed = true;
    [ObservableProperty] private bool _showNotRun = true;

    /// <summary>テスト名の絞り込み文字列（完全名に含むで照合・大小無視）。</summary>
    [ObservableProperty] private string _testFilter = "";

    /// <summary>xUnit Trait／NUnit Category／MSTest TestCategoryの絞り込み文字列。
    /// 公式adapterで補完された行も、取得できたタグを同じ検索対象にする。</summary>
    [ObservableProperty] private string _traitFilter = "";

    /// <summary>フィルタ適用後に表示できるテストが 1 件でもあるか（ツリー再構築で更新）。</summary>
    [ObservableProperty] private bool _hasVisibleTests;

    /// <summary>テストはあるがフィルタで全て隠れているか（「該当なし」案内の出し分け）。</summary>
    public bool NoFilterMatch => HasTestResults && !HasVisibleTests;

    partial void OnShowPassedChanged(bool value) => SyncTree();
    partial void OnShowFailedChanged(bool value) => SyncTree();
    partial void OnShowNotRunChanged(bool value) => SyncTree();
    partial void OnTestFilterChanged(string value) => SyncTree();
    partial void OnTraitFilterChanged(string value) => SyncTree();
    partial void OnHasVisibleTestsChanged(bool value) => OnPropertyChanged(nameof(NoFilterMatch));
    partial void OnHasTestResultsChanged(bool value) => OnPropertyChanged(nameof(NoFilterMatch));
    partial void OnIsDiscoveringTestsChanged(bool value) => OnPropertyChanged(nameof(TestEmptyHint));

    /// <summary>テスト一覧（フラットな全件。突き合わせ・集計の元データ）。表示は <see cref="TestTree"/>。</summary>
    public ObservableCollection<TestItemViewModel> Tests { get; } = new();
    /// <summary>クラス単位にまとめたテストツリー（TreeView の表示元）。<see cref="SyncTree"/> で再構築する。</summary>
    public ObservableCollection<TestGroupViewModel> TestTree { get; } = new();

    internal DebugTestsViewModel(IWorkspaceService workspace, ITerminalService terminal,
        ITestDiscoveryService testDiscovery, DebugViewModel manager,
        ISolutionModelService? solutionModel = null)
    {
        _workspace = workspace;
        _terminal = terminal;
        _testDiscovery = testDiscovery;
        _manager = manager;
        _session = manager;
        _solutionModel = solutionModel;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _session.SessionStateChanged += () =>
        {
            TestCommand.NotifyCanExecuteChanged();
        RunSingleTestCommand.NotifyCanExecuteChanged();
        RunGroupCommand.NotifyCanExecuteChanged();
        DebugSingleTestCommand.NotifyCanExecuteChanged();
        DebugGroupCommand.NotifyCanExecuteChanged();
        DebugFileCommand.NotifyCanExecuteChanged();
        DiscoverWithTestAdapterCommand.NotifyCanExecuteChanged();
        RunCoverageCommand.NotifyCanExecuteChanged();
        };

        // テストはバックグラウンドで自動収集する。ワークスペースを開いた時点とソース変更を契機に高速探索で更新。
        // 複数フォルダー時は RootChanged／FoldersChanged のどちらでも全フォルダーを監視し直す。
        _workspace.RootChanged += OnWorkspaceRootChanged;
        _workspace.FoldersChanged += OnWorkspaceFoldersChanged;
        if (_solutionModel is not null)
            _solutionModel.Changed += OnSolutionModelChanged;
        RewatchFolders();
        if (_workspace.Folders.Count > 0) _ = DiscoverTestsAsync();
    }

    public void Dispose()
    {
        _workspace.RootChanged -= OnWorkspaceRootChanged;
        _workspace.FoldersChanged -= OnWorkspaceFoldersChanged;
        if (_solutionModel is not null)
            _solutionModel.Changed -= OnSolutionModelChanged;
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        StopTestDebugProcessAsync().GetAwaiter().GetResult();
    }

    private void RewatchFolders()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        foreach (var folder in _workspace.Folders)
        {
            var w = new TestSourceWatcher(_dispatcher);
            w.Changed += () => _ = DiscoverTestsAsync();
            w.Watch(folder);
            _watchers.Add(w);
        }
    }

    /// <summary>一覧・各行の状態が変わったとき（UI スレッド）。エディタのガターのテストグリフ再送の契機。</summary>
    public event Action? TestsChanged;
    public event Action? CoverageChanged;

    IReadOnlyList<TestItemViewModel> ITestExplorer.TestItems => Tests;

    /// <summary>ガターの ▶／コマンドパレットからの単体実行。ビルド中・デバッグ中は何もしない
    /// （テストペインの ▶ と同じ <see cref="CanRunTask"/> の判定を通す）。</summary>
    async Task<bool> ITestExplorer.RunTestAsync(TestItemViewModel test)
    {
        if (!RunSingleTestCommand.CanExecute(test)) return false;
        await RunSingleTestCommand.ExecuteAsync(test);
        return true;
    }

    /// <summary>テストタブが表示されたときの保険的な収集（まだ一覧が無ければバックグラウンド収集を起動する）。</summary>
    public void EnsureTestsDiscovered()
    {
        if (Tests.Count == 0 && !IsDiscoveringTests) _ = DiscoverTestsAsync();
    }

    /// <summary>失敗テストのダブルクリック：スタックトレースから拾った位置へジャンプする（通常タブ＋フォーカス）。</summary>
    public void NavigateToTestSource(TestItemViewModel? t)
    {
        if (t is { HasSource: true, SourcePath: { } p })
            _session.RaiseFrameActivated(p, t.Line - 1);  // 1始まり → エディタ 0始まり
    }

    /// <summary>ワークスペースが変わったら監視を張り替え、収集し直す。起動時はこのイベントが
    /// 初フレーム後のハイドレート中（エディタ／ブラウザ実体化）に発火するため、Background 優先度で
    /// 後回しにして復元を割り込まない（テスト一覧は IDE ペインを開くまで見えないので即時性は不要）。</summary>
    private void OnWorkspaceRootChanged(object? sender, string? root)
        => _dispatcher.InvokeAsync(() => { RewatchFolders(); _ = DiscoverTestsAsync(); },
            DispatcherPriority.Background);

    private void OnWorkspaceFoldersChanged(object? sender, EventArgs e)
        => _dispatcher.InvokeAsync(() => { RewatchFolders(); _ = DiscoverTestsAsync(); },
            DispatcherPriority.Background);

    /// <summary>構成／TFM変更でMSBuildのCompile対象が変わったとき、テスト一覧も同じ世代へ更新する。
    /// Loading中は一度空にし、前構成のテストを現在構成の結果として見せない。</summary>
    private void OnSolutionModelChanged(object? sender, SolutionModel model)
        => _dispatcher.InvokeAsync(() => _ = DiscoverTestsAsync(), DispatcherPriority.Background);

    /// <summary>ソース走査でテスト一覧を収集する（ビルドを伴わない・バックグラウンド）。複数フォルダー時は
    /// 全フォルダーを走査して結果をマージする。探索中に来た要求は1回にまとめて末尾でもう一度回す
    /// （編集中の連続変更で重複起動しない）。</summary>
    private async Task DiscoverTestsAsync()
    {
        var folders = _workspace.Folders;
        if (IsDiscoveringTests) { _rediscoverRequested = true; return; }

        IsDiscoveringTests = true;
        try
        {
            do
            {
                _rediscoverRequested = false;
                IReadOnlyList<DiscoveredTest> found;
                var solution = _solutionModel?.Current;
                // Ready／Loading／Failed はMSBuild評価済みモデルを正とし、空集合も完全な結果として扱う。
                // NotConfigured の間だけ従来のフォルダー走査へフォールバックする。
                var replaceExisting = solution is { State: ProjectLoadState.Ready or ProjectLoadState.Loading
                    or ProjectLoadState.Failed } || folders.Count == 0;
                try
                {
                    found = solution is { State: ProjectLoadState.Loading or ProjectLoadState.Failed }
                        ? Array.Empty<DiscoveredTest>()
                        : solution is { State: ProjectLoadState.Ready }
                            ? await Task.Run(() => _testDiscovery.Discover(solution))
                            : await Task.Run(() => folders.SelectMany(_testDiscovery.Discover).ToList());
                }
                catch { found = Array.Empty<DiscoveredTest>(); }
                // 走査の resume が UI 以外で来ても、コレクション更新は必ず UI スレッドで行う。
                await _dispatcher.InvokeAsync(() => ApplyDiscovered(found, replaceExisting));
            } while (_rediscoverRequested);
        }
        finally { IsDiscoveringTests = false; }
    }

    /// <summary>収集結果を既存の一覧へマージする。新規は追加、消えた未実行テストは除去する。
    /// <paramref name="authoritative"/> の場合はMSBuildのCompile集合が正なので、実行済みでも消えた行を除去する。
    /// フォルダー走査の既存モードでは、パーサが拾えない種別・直前の実行結果を保持する。
    /// <para>マージ規則（宣言位置は毎回更新、失敗位置は温存）を固定したいので単体テストから直接呼ぶ＝internal。</para></summary>
    internal void ApplyDiscovered(IReadOnlyList<DiscoveredTest> found, bool authoritative = false)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var existing = new Dictionary<string, TestItemViewModel>(StringComparer.Ordinal);
        foreach (var t in Tests) existing[t.FullyQualifiedName] = t;

        foreach (var d in found)
        {
            keep.Add(d.FullyQualifiedName);
            if (!existing.TryGetValue(d.FullyQualifiedName, out var item))
            {
                item = new TestItemViewModel(d.FullyQualifiedName);
                Tests.Add(item);
            }
            item.ApplyDiscoveryMetadata(d.IsParameterized, d.SkipReason, d.Traits, d.Cases);
            // 宣言位置はエディタのガターの ▶ を置く場所。行の挿入・削除には追従しないので、
            // 走査のたびに上書きして最新へ寄せる（失敗位置＝SourcePath/Line とは別枠）。
            item.DeclarationPath = d.SourcePath;
            item.DeclarationLine = d.Line1;
        }

        // MSBuildの正確なCompile集合を使う場合は、構成／TFM切替で消えた実行済み行も掃除する。
        for (var i = Tests.Count - 1; i >= 0; i--)
        {
            var t = Tests[i];
            if (keep.Contains(t.FullyQualifiedName)) continue;
            if (!authoritative && t.Status != TestStatus.NotRun) continue;
            Tests.RemoveAt(i);
        }

        SyncTree();
        RecomputeSummary();
        TestsChanged?.Invoke();
    }

    /// <summary>ワークスペースの全テストを実行する（<c>dotnet test</c> ＋ TRX ロガー）。結果を各行へ反映する。</summary>
    [RelayCommand(CanExecute = nameof(CanRunTask))]
    private async Task Test()
    {
        var target = _session.FindBuildTarget();
        if (target is null) return;
        _session.RequestOutput();  // 押下時に即「出力」へ
        await RunCoreAsync(target, Tests.ToList(), null, "テスト実行中…", $"テスト: {Path.GetFileName(target)}",
            null, had => CountStatus(had, Tests));
    }

    /// <summary>失敗したテストだけを、メソッド単位のORフィルターで再実行する。</summary>
    [RelayCommand(CanExecute = nameof(CanRerunFailed))]
    private async Task RerunFailed()
    {
        var failed = Tests.Where(t => t.Status == TestStatus.Failed).ToList();
        if (failed.Count == 0) return;
        var target = _session.FindBuildTarget();
        if (target is null) return;

        var filter = CSharpTestExecutionService.BuildFullyQualifiedNameFilter(
            failed.Select(test => test.FilterExpression));
        await RunCoreAsync(target, failed, filter, "失敗テストを再実行中…", "失敗テストの再実行",
            UpdateAggregates, had => CountStatus(had, failed));
    }

    /// <summary>1 件のテストだけ実行する（<c>--filter "FullyQualifiedName=..."</c>）。テオリは同メソッドの全ケースが対象。</summary>
    [RelayCommand(CanExecute = nameof(CanRunTask))]
    private async Task RunSingleTest(TestItemViewModel? item)
    {
        if (item is null) return;
        var target = _session.FindBuildTarget();
        if (target is null) return;
        await RunCoreAsync(target, new[] { item },
            CSharpTestExecutionService.BuildFullyQualifiedNameFilter([item.FilterExpression]),
            $"テスト実行中… {item.DisplayName}", $"テスト: {item.DisplayName}", UpdateAggregates, had => item.Status switch
            {
                TestStatus.Failed => "テスト失敗",
            TestStatus.Passed => "テスト成功",
                _ => had ? "テスト完了" : "テスト結果を取得できませんでした",
            });
    }

    /// <summary>1件のC#テストをtesthostで待機させ、既存のnetcoredbgへattachする。</summary>
    [RelayCommand(CanExecute = nameof(CanRunTask))]
    private async Task DebugSingleTest(TestItemViewModel? item)
    {
        if (item is null) return;
        await DebugTestsAsync(new[] { item }, $"テストデバッグ: {item.DisplayName}");
    }

    /// <summary>クラス内のC#テストをtesthostで待機させ、既存のnetcoredbgへattachする。</summary>
    [RelayCommand(CanExecute = nameof(CanRunTask))]
    private async Task DebugGroup(TestGroupViewModel? group)
    {
        if (group is null) return;
        await DebugTestsAsync(group.Tests.ToList(), $"テストデバッグ: {group.Name}");
    }

    /// <summary>同じ宣言ファイルに属するC#テストをtesthostで待機させ、既存のnetcoredbgへattachする。</summary>
    [RelayCommand(CanExecute = nameof(CanRunTask))]
    private async Task DebugFile(TestGroupViewModel? group)
    {
        if (group?.SourcePath is not { Length: > 0 } path) return;
        var fileTests = Tests.Where(test => string.Equals(
            test.DeclarationPath, path, StringComparison.OrdinalIgnoreCase)).ToList();
        if (fileTests.Count == 0) return;
        await DebugTestsAsync(fileTests, $"テストデバッグ: {Path.GetFileName(path)}");
    }

    /// <summary>Solution Explorerからプロジェクト／ソリューション範囲のテストデバッグを開始する。</summary>
    internal Task DebugProjectTestsAsync(string targetPath, bool solutionScope)
    {
        var fullTarget = Path.GetFullPath(targetPath);
        var tests = solutionScope
            ? Tests.ToList()
            : Tests.Where(test => test.DeclarationPath is { Length: > 0 } declaration
                && _solutionModel?.ProjectForFile(declaration)?.FullPath is { } projectPath
                && string.Equals(Path.GetFullPath(projectPath), fullTarget,
                    StringComparison.OrdinalIgnoreCase)).ToList();
        return DebugTestsAsync(tests, solutionScope
            ? "ソリューションのテストデバッグ"
            : $"プロジェクトのテストデバッグ: {Path.GetFileName(targetPath)}");
    }

    private async Task DebugTestsAsync(IReadOnlyList<TestItemViewModel> tests, string label)
    {
        if (tests.Count == 0) return;
        var declarationPaths = tests.Select(test => test.DeclarationPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (declarationPaths.Length == 0)
        {
            _session.StatusMessage = "テストの宣言ファイルを解決できません";
            return;
        }

        var projectByPath = declarationPaths.ToDictionary(
            path => path, path => _solutionModel?.ProjectForFile(path),
            StringComparer.OrdinalIgnoreCase);
        if (projectByPath.Values.Any(project => project is null) ||
            projectByPath.Values.Any(project => project is not { IsTestProject: true }))
        {
            _session.StatusMessage = "テストプロジェクトを解決できません";
            return;
        }
        var projects = projectByPath.Values.Cast<ProjectModel>()
            .GroupBy(project => project.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToArray();

        _session.RequestOutput();
        _session.IsTaskRunning = true;
        CSharpTestDebugProcess? runner = null;
        try
        {
            await StopTestDebugProcessAsync();
            _session.StatusMessage = $"{label}を準備中…";
            var assemblies = new List<string>();
            foreach (var project in projects)
            {
                var targetFramework = project.SelectedTargetFramework;
                var build = await CSharpBuildService.RunAsync(
                    _terminal, project.FullPath, project.Configuration, CancellationToken.None, targetFramework);
                _session.WriteConsole(build.Output);
                _session.ReportBuildOutput(build.Output);
                if (!build.Success)
                {
                    _session.StatusMessage = $"テストデバッグ用ビルドに失敗しました（{build.ExitCode}）";
                    return;
                }

                var assembly = await CSharpTestDebugTargetResolver.ResolveAssemblyPathAsync(
                    project.FullPath, targetFramework, project.Configuration);
                if (assembly is null)
                {
                    _session.StatusMessage = "テストアセンブリを解決できません";
                    return;
                }
                assemblies.Add(assembly);
            }

            runner = await CSharpTestDebugProcess.StartAsync(
                assemblies, CSharpTestExecutionService.BuildFullyQualifiedNameFilter(
                    tests.Select(test => test.FilterExpression)),
                projects.Length == 1 ? projects[0].Directory : _solutionModel?.Current.RootDirectory,
                CancellationToken.None);
            _testDebugProcess = runner;
            var capturedRunner = runner;
            runner.Output += line => _dispatcher.BeginInvoke(new Action(() =>
                _session.Append(DebugOutputCategory.Console, line)));
            runner.Exited += code => _dispatcher.BeginInvoke(new Action(() =>
            {
                if (ReferenceEquals(_testDebugProcess, capturedRunner))
                    _session.Append(DebugOutputCategory.Important,
                        $"テストデバッグ用testhost終了（exit {code}）");
            }));
            var pid = runner.TestHostProcessId;
            if (pid is null)
            {
                _session.StatusMessage = "testhostのPIDを取得できません";
                return;
            }

            _session.IsTaskRunning = false;
            var attachedSession = await _manager.AttachTestProcessAsync(pid.Value, label);
            if (attachedSession is null)
            {
                _session.StatusMessage = "テストデバッグのattachに失敗しました";
                await StopTestDebugProcessAsync();
                return;
            }
            if (!TrackTestDebugSession(attachedSession))
            {
                _session.StatusMessage = "テストデバッグのセッションが終了しました";
                await StopTestDebugProcessAsync();
                return;
            }
            _session.StatusMessage = $"{label}中";
            runner = null; // セッションがtesthostを使用中。フィールドで保持する。
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _session.Append(DebugOutputCategory.Important, $"テストデバッグを開始できません: {ex.Message}");
        }
        finally
        {
            _session.IsTaskRunning = false;
            if (runner is not null) await runner.DisposeAsync();
        }
    }

    private async Task StopTestDebugProcessAsync()
    {
        var session = Interlocked.Exchange(ref _testDebugSession, null);
        if (session is not null)
            session.DebugService.Exited -= OnTestDebugSessionExited;

        var process = Interlocked.Exchange(ref _testDebugProcess, null);
        if (process is not null) await process.DisposeAsync();
    }

    /// <summary>testhostをattachしたDAPセッションとVSTest待機プロセスを結び付ける。
    /// DAP側が先に終了した場合もtesthost待機プロセスを残さない。</summary>
    private bool TrackTestDebugSession(DebugSessionViewModel session)
    {
        var previous = Interlocked.Exchange(ref _testDebugSession, session);
        if (previous is not null)
            previous.DebugService.Exited -= OnTestDebugSessionExited;
        session.DebugService.Exited += OnTestDebugSessionExited;

        // Attachが瞬時に失敗／終了した場合、購読より先にイベントが到着する競合を
        // 防ぐため、現在状態も確認して同じ後始末へ送る。
        if (session.DebugService.State is DebugSessionState.Idle or DebugSessionState.Terminated or DebugSessionState.Failed)
        {
            OnTestDebugSessionExited(session.DebugService, new DebugExited(null, "testhost attach ended"));
            return false;
        }

        return true;
    }

    private void OnTestDebugSessionExited(object? sender, DebugExited exited)
    {
        var session = Volatile.Read(ref _testDebugSession);
        if (session is null || !ReferenceEquals(session.DebugService, sender)) return;
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _testDebugSession, null, session), session)) return;
        session.DebugService.Exited -= OnTestDebugSessionExited;
        _ = DisposeExitedTestDebugProcessAsync();
    }

    private async Task DisposeExitedTestDebugProcessAsync()
    {
        var process = Interlocked.Exchange(ref _testDebugProcess, null);
        if (process is null) return;
        try { await process.DisposeAsync(); }
        catch (Exception ex)
        {
            await _dispatcher.InvokeAsync(() =>
                _session.Append(DebugOutputCategory.Important, $"テストデバッグ後始末に失敗しました: {ex.Message}"));
        }
    }

    /// <summary>公式test adapterの検出結果を明示的に取得する。ソース走査より遅いため自動実行はせず、
    /// 実行環境が生成する実際のテスト名・ケースを既存一覧へ補完する。</summary>
    [RelayCommand(CanExecute = nameof(CanRunTask))]
    private async Task DiscoverWithTestAdapter()
    {
        var target = _session.FindBuildTarget();
        if (target is null) return;

        _session.IsTaskRunning = true;
        try
        {
            _session.RequestOutput();
            _session.StatusMessage = "公式テスト検出中…";
            var result = await CSharpTestExecutionService.RunListTestsAsync(
                _terminal, target, ConfigurationFor(target), CancellationToken.None,
                targetFramework: SelectedTargetFrameworkFor(target));
            _session.WriteConsole(result.Output);
            _session.ReportBuildOutput(result.Output);
            var found = TestAdapterOutputParser.Parse(result.Output);
            await _dispatcher.InvokeAsync(() => ApplyAdapterDiscovered(found));
            _session.StatusMessage = found.Count > 0
                ? $"公式テスト検出: {found.Count}件"
                : "公式テストを検出できませんでした";
        }
        finally { _session.IsTaskRunning = false; }
    }

    /// <summary>選択中のsolution構成でcoverletのXPlat Code Coverageを実行し、Cobertura／OpenCoverの要約を表示する。</summary>
    [RelayCommand(CanExecute = nameof(CanRunTask))]
    private async Task RunCoverage()
    {
        var target = _session.FindBuildTarget();
        if (target is null) return;

        _session.IsTaskRunning = true;
        string? coverageDirectory = null;
        CoverageSummary = "";
        CoverageFiles = Array.Empty<CoverageFileSummary>();
        OnPropertyChanged(nameof(CoverageFiles));
        IsCoverageDetailsVisible = false;
        CoverageChanged?.Invoke();
        try
        {
            _session.RequestOutput();
            _session.StatusMessage = "カバレッジ収集中…";
            var run = await DotnetTestRunner.RunCoverageAsync(
                _terminal, _session, target, ConfigurationFor(target),
                SelectedTargetFrameworkFor(target));
            if (run is null)
            {
                _session.StatusMessage = "カバレッジを実行できませんでした";
                return;
            }

            coverageDirectory = run.Value.Directory;
            var reportPath = CoverageReportParser.FindReport(run.Value.Directory);
            if (reportPath is null)
            {
                CoverageSummary = "カバレッジ結果なし（coverlet.collectorを確認してください）";
                _session.StatusMessage = $"カバレッジ結果なし（exit {run.Value.ExitCode}）";
                return;
            }

            var report = CoverageReportParser.ParseFile(reportPath, out var error);
            if (report is null)
            {
                CoverageSummary = $"カバレッジ解析失敗: {error}";
                _session.StatusMessage = "カバレッジ解析に失敗しました";
                return;
            }

            var percent = report.LineRate * 100;
            CoverageFiles = report.Files;
            OnPropertyChanged(nameof(CoverageFiles));
            CoverageChanged?.Invoke();
            var branchText = report.ValidBranches > 0
                ? $"、分岐 {report.BranchRate * 100:0.0}%（{report.CoveredBranches}/{report.ValidBranches}）"
                : "";
            CoverageSummary = $"カバレッジ {percent:0.0}%（{report.CoveredLines}/{report.ValidLines} 行{branchText}、{report.Files.Count} ファイル、{report.Format}）";
            _session.StatusMessage = run.Value.ExitCode == 0
                ? "カバレッジ取得完了"
                : $"カバレッジ取得完了（テスト exit {run.Value.ExitCode}）";
        }
        finally
        {
            CSharpTestExecutionService.CleanupCoverageResults(coverageDirectory);
            _session.IsTaskRunning = false;
        }
    }

    /// <summary>クラスグループ内のテストをまとめて実行する（<c>--filter "FullyQualifiedName~Namespace.Class."</c>）。</summary>
    [RelayCommand(CanExecute = nameof(CanRunTask))]
    private async Task RunGroup(TestGroupViewModel? group)
    {
        if (group is null) return;
        var target = _session.FindBuildTarget();
        if (target is null) return;
        await RunCoreAsync(target, group.Tests.ToList(), $"FullyQualifiedName~{group.Key}.",
            $"テスト実行中… {group.Name}", $"テスト: {group.Name}", group.RecomputeAggregate,
            had => CountStatus(had, group.Tests));
    }

    /// <summary>同じ宣言ファイルに属するC#テストをまとめて実行する。
    /// クラス境界をまたぐため、FQNをORで列挙し、ファイル名の部分一致には依存しない。</summary>
    [RelayCommand(CanExecute = nameof(CanRunTask))]
    private async Task RunFile(TestGroupViewModel? group)
    {
        if (group?.SourcePath is not { Length: > 0 } path) return;
        var target = _session.FindBuildTarget();
        if (target is null) return;
        var fileTests = Tests.Where(test => string.Equals(
            test.DeclarationPath, path, StringComparison.OrdinalIgnoreCase)).ToList();
        if (fileTests.Count == 0) return;

        await RunCoreAsync(target, fileTests,
            CSharpTestExecutionService.BuildFullyQualifiedNameFilter(
                fileTests.Select(test => test.FilterExpression)),
            $"テスト実行中… {Path.GetFileName(path)}", $"テスト: {Path.GetFileName(path)}",
            UpdateAggregates, had => CountStatus(had, fileTests));
    }

    /// <summary>テスト実行の共通処理：対象行を実行中にし、<c>dotnet test</c>→TRX 反映→未突合の戻し→ツリー/集計更新→状態文言。</summary>
    private async Task RunCoreAsync(string target, IReadOnlyList<TestItemViewModel> running, string? filter,
        string runningStatus, string label, Action? prepare, Func<bool, string> finalStatus)
    {
        _session.IsTaskRunning = true;
        CSharpTestExecutionResult? execution = null;
        try
        {
            _session.StatusMessage = runningStatus;
            foreach (var t in running) t.SetRunning();
            prepare?.Invoke();
            TestsChanged?.Invoke();   // 実行中グリフ（…）をガターへすぐ出す
            execution = await DotnetTestRunner.RunAsync(_terminal, _session, target, filter, label,
                ConfigurationFor(target), SelectedTargetFrameworkFor(target));
            if (execution?.TrxPath is { } trxPath)
                DotnetTestRunner.ApplyTrx(trxPath, _session, Tests);
            _session.StatusMessage = finalStatus(execution?.TrxPath is not null);
        }
        finally
        {
            if (execution is not null)
                CSharpTestExecutionService.CleanupResults(execution);
            // 後始末は必ず通す。例外・中断でここを飛ばすと「実行中」のままの行が残り、
            // エディタのガターも実行中の塗り＋「実行中…」のツールチップで固まる。
            foreach (var t in running) if (t.Status == TestStatus.Running) t.ResetStatus();  // 未突合は戻す
            SyncTree();
            RecomputeSummary();
            TestsChanged?.Invoke();
            _session.IsTaskRunning = false;
        }
    }

    private static string CountStatus(bool hadResults, IEnumerable<TestItemViewModel> set)
    {
        if (!hadResults) return "テスト結果を取得できませんでした";
        var failed = set.Count(t => t.Status == TestStatus.Failed);
        return failed == 0 ? "テスト成功" : $"テスト失敗（{failed} 件）";
    }

    private bool CanRunTask() => !_session.IsBusy && !_session.IsTaskRunning;

    private bool CanRerunFailed() => CanRunTask() && FailedCount > 0;

    private string ConfigurationFor(string? targetPath)
        => _solutionModel?.Current.ConfigurationForTarget(targetPath) ?? "Debug";

    private string? SelectedTargetFrameworkFor(string target)
        => _solutionModel?.ProjectForTarget(target)?.SelectedTargetFramework;

    /// <summary>Solution Explorerから実行したC#テストのTRX結果を、このTest Explorerへ反映する。
    /// 実行経路が異なっても、名前突合せ・ケース集約・ツリー・集計・ガター通知を同じ処理へ揃える。</summary>
    internal void ApplyExternalExecutionResult(CSharpTestExecutionResult execution)
    {
        if (execution.TrxPath is { } trxPath)
            DotnetTestRunner.ApplyTrx(trxPath, _session, Tests);
        SyncTree();
        RecomputeSummary();
        TestsChanged?.Invoke();
    }

    /// <summary>公式検出の名前を既存のソース検出行へ補完する。既存の宣言位置・結果・属性メタデータは温存する。</summary>
    internal void ApplyAdapterDiscovered(IReadOnlyList<DiscoveredTest> found)
    {
        foreach (var d in found)
        {
            var existing = Tests.FirstOrDefault(t =>
                string.Equals(t.FilterExpression, d.FullyQualifiedName, StringComparison.Ordinal));
            if (existing is not null)
            {
                existing.ApplyAdapterCases(d.IsParameterized, d.Cases);
                continue;
            }

            var item = new TestItemViewModel(d.FullyQualifiedName);
            item.ApplyDiscoveryMetadata(d.IsParameterized, d.SkipReason, d.Traits, d.Cases);
            Tests.Add(item);
        }
        SyncTree();
        RecomputeSummary();
        TestsChanged?.Invoke();
    }

    /// <summary>一覧の各行ステータスから集計（成功/失敗/スキップ/合計）を作り直し、案内の出し分けも更新する。</summary>
    private void RecomputeSummary()
    {
        HasTestResults = Tests.Count > 0;
        FailedCount = Tests.Count(t => t.Status == TestStatus.Failed);
        RerunFailedCommand.NotifyCanExecuteChanged();
        if (!HasTestResults) { TestSummary = ""; return; }

        var passed = Tests.Count(t => t.Status == TestStatus.Passed);
        var failed = FailedCount;
        var skipped = Tests.Count(t => t.Status == TestStatus.Skipped);
        TestSummary = $"成功 {passed} / 失敗 {failed} / スキップ {skipped} / 合計 {Tests.Count}";
    }

    /// <summary>フラットな <see cref="Tests"/> をクラス単位のツリーへ再構築する。展開状態は <see cref="TestGroupViewModel.Key"/>
    /// で引き継ぐ（葉は同一インスタンスを使い回すのでステータスのバインドは保たれる）。</summary>
    private void SyncTree()
    {
        var expanded = TestTree.ToDictionary(g => g.Key, g => g.IsExpanded);
        TestTree.Clear();

        // 状態トグルやテキスト検索で絞り込み中は、一致が埋もれないよう全グループを開く。
        var filtering = !string.IsNullOrEmpty(TestFilter?.Trim())
            || !string.IsNullOrEmpty(TraitFilter?.Trim())
            || !(ShowPassed && ShowFailed && ShowNotRun);

        foreach (var g in Tests.GroupBy(t => t.ClassName).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var visible = g.Where(MatchesFilter).OrderBy(t => t.DisplayName, StringComparer.Ordinal).ToList();
            if (visible.Count == 0) continue;  // フィルタで全部隠れたクラスは出さない

            var name = g.Key.Length == 0 ? "(その他)" : g.Key[(g.Key.LastIndexOf('.') + 1)..];
            var sourcePath = g.Select(test => test.DeclarationPath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
            var node = new TestGroupViewModel(g.Key, name, sourcePath);
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

        var nameFilter = TestFilter?.Trim();
        if (!string.IsNullOrEmpty(nameFilter) &&
            !t.FullyQualifiedName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        var traitFilter = TraitFilter?.Trim();
        return string.IsNullOrEmpty(traitFilter)
            || t.TraitsText.Contains(traitFilter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>葉のステータスだけ変えたとき（実行開始時など）にグループの集計を更新する。</summary>
    private void UpdateAggregates()
    {
        foreach (var g in TestTree) g.RecomputeAggregate();
    }
}
