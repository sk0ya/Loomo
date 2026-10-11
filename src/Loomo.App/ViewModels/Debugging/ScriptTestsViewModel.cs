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
using sk0ya.Loomo.Services.Testing;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>スクリプト系テストエクスプローラ 1 枚ぶんの設定（どのランナーを、どの探索で、どう案内するか）。
/// TS IDE ペイン（vitest / jest）と IDE ペインの pytest 枠が同じ <see cref="ScriptTestsViewModel"/> をこれで使い分ける。</summary>
internal sealed record ScriptTestProfile(
    string LanguageLabel,
    IReadOnlyList<ITestRunner> Runners,
    Func<string, IReadOnlyList<ScriptDiscoveredTest>> Discover,
    string[] WatchPatterns,
    string[] WatchIgnoreDirs,
    string EmptyHint,
    bool RequireProject,
    string MissingRunnerMessage,
    Func<string, string> LeafOf)
{
    /// <summary>TS IDE ペイン：vitest / jest。探索は <see cref="TsTestDiscovery"/>。テストはランナーが特定できなくても
    /// 一覧に出す（従来どおり。実行時に「特定できません」と理由を出す）。</summary>
    public static ScriptTestProfile TypeScript { get; } = new(
        "TypeScript",
        [new VitestRunner(), new JestRunner()],
        root => TsTestDiscovery.Discover(root)
            .Select(d => new ScriptDiscoveredTest(d.FilePath, d.Title, d.Line1, d.IsEach)).ToList(),
        TsTestDiscovery.TestFilePatterns,
        ["node_modules", "dist", "build", "coverage"],
        "テストが見つかりませんでした（*.test.ts / *.spec.ts 等。ソース変更で自動収集します）。",
        RequireProject: false,
        "テストランナー（vitest / jest）を package.json・設定ファイルから特定できません",
        // 葉＝describe 前置を落としたタイトル（-t に渡す名前）。
        title => title.LastIndexOf(" > ", StringComparison.Ordinal) is var at and >= 0 ? title[(at + 3)..] : title);

    /// <summary>IDE ペインの pytest 枠。Python のプロジェクトだと判る手掛かり（<see cref="PytestRunner.Detect"/>）の
    /// 無い場所の test_*.py は一覧に出さない——.NET リポジトリの補助スクリプトでテスト枠が現れないように。</summary>
    public static ScriptTestProfile Python { get; } = new(
        "Python",
        [new PytestRunner()],
        PytestDiscovery.Discover,
        PytestDiscovery.TestFilePatterns,
        [".venv", "venv", "env", "__pycache__", ".pytest_cache", ".tox", "node_modules", "build", "dist"],
        "テストが見つかりませんでした（test_*.py / *_test.py。ソース変更で自動収集します）。",
        RequireProject: true,
        "pytest のプロジェクト（pytest.ini / pyproject.toml / conftest.py 等）を特定できません",
        // ノード ID 後半（TestCls::test_x）がそのまま葉であり、1 件指定でもある。
        title => title);
}

/// <summary>スクリプト系（vitest / jest / pytest）のテストエクスプローラのサブ ViewModel（<see cref="DebugTestsViewModel"/> の
/// ランナー非依存版）。ソース走査で自動収集し、<see cref="ITestRunner"/> のレポート（<see cref="ScriptTestRunner"/>）で
/// 各行のステータスを更新する。公開メンバー名は dotnet 版と揃えてあり、ビュー（DebugTestsView）をそのまま共有する。
/// グループ＝テストファイル、葉＝テストタイトル（JS は describe 連結、pytest はノード ID 後半）。
/// <para>どのランナーで走らせるかはファイルごとに <see cref="TestProjectLocator"/> が決める（ワークスペースフォルダー単位、
/// マルチルート対応）。全実行はプロジェクトごとに 1 回ずつ走らせて結果をまとめる。</para></summary>
public sealed partial class ScriptTestsViewModel : ObservableObject, ITestExplorer, IDisposable
{
    /// <summary>共有テストビューのデバッグ列。デバッガで起動する口（TS IDE ペインの js-debug）を渡されたときだけ出す
    /// ——vitest / jest。pytest は debugpy 経路が未実装なので隠す。</summary>
    public bool IsTestDebugSupported => _debugLauncher is not null;

    /// <summary>共有テストビューの dotnet 専用の操作（公式検出・カバレッジ）を隠す。失敗の再実行はこちらにもある。</summary>
    public bool IsDotnetToolsVisible => false;

    private readonly IWorkspaceService _workspace;
    private readonly ITerminalService _terminal;
    private readonly IDebugSession _session;
    private readonly ScriptTestProfile _profile;
    private readonly Func<TestDebugLaunch, string, Task<IDebugService?>>? _debugLauncher;
    private readonly Dispatcher _dispatcher;
    private readonly List<TestSourceWatcher> _watchers = new();

    /// <summary>探索中に来た再収集要求（探索完了後にもう一度走らせる）。</summary>
    private bool _rediscoverRequested;
    private int _discoveryGeneration;

    [ObservableProperty] private string _testSummary = "";
    [ObservableProperty] private bool _hasTestResults;
    /// <summary>直近の一覧にある失敗テスト数。失敗再実行コマンドの有効状態にも使う。</summary>
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private bool _isDiscoveringTests;

    public string TestEmptyHint => IsDiscoveringTests ? "テストを収集しています…" : _profile.EmptyHint;

    [ObservableProperty] private bool _showPassed = true;
    [ObservableProperty] private bool _showFailed = true;
    [ObservableProperty] private bool _showNotRun = true;

    [ObservableProperty] private string _testFilter = "";

    /// <summary>共有テストビューのタグ絞り込み。現在のスクリプト系の探索はタグを返さないが、C# 版と同じ表示契約を保つ。</summary>
    [ObservableProperty] private string _traitFilter = "";

    [ObservableProperty] private bool _hasVisibleTests;

    public bool NoFilterMatch => HasTestResults && !HasVisibleTests;

    partial void OnShowPassedChanged(bool value) => SyncTree();
    partial void OnShowFailedChanged(bool value) => SyncTree();
    partial void OnShowNotRunChanged(bool value) => SyncTree();
    partial void OnTestFilterChanged(string value) => SyncTree();
    partial void OnTraitFilterChanged(string value) => SyncTree();
    partial void OnHasVisibleTestsChanged(bool value) => OnPropertyChanged(nameof(NoFilterMatch));
    partial void OnHasTestResultsChanged(bool value) => OnPropertyChanged(nameof(NoFilterMatch));
    partial void OnIsDiscoveringTestsChanged(bool value) => OnPropertyChanged(nameof(TestEmptyHint));

    /// <summary>テスト一覧（フラットな全件）。FQN＝正規化ファイルパス::タイトル。</summary>
    public ObservableCollection<TestItemViewModel> Tests { get; } = new();
    /// <summary>ファイル単位にまとめたテストツリー（TreeView の表示元）。</summary>
    public ObservableCollection<TestGroupViewModel> TestTree { get; } = new();

    internal ScriptTestsViewModel(IWorkspaceService workspace, ITerminalService terminal, IDebugSession session,
        ScriptTestProfile profile, Func<TestDebugLaunch, string, Task<IDebugService?>>? debugLauncher = null)
    {
        _debugLauncher = debugLauncher;
        _workspace = workspace;
        _terminal = terminal;
        _session = session;
        _profile = profile;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _session.SessionStateChanged += () =>
        {
            TestCommand.NotifyCanExecuteChanged();
            RerunFailedCommand.NotifyCanExecuteChanged();
            RunSingleTestCommand.NotifyCanExecuteChanged();
            RunGroupCommand.NotifyCanExecuteChanged();
            DebugSingleTestCommand.NotifyCanExecuteChanged();
            DebugGroupCommand.NotifyCanExecuteChanged();
            DebugFileCommand.NotifyCanExecuteChanged();
        };

        _workspace.RootChanged += OnWorkspaceRootChanged;
        _workspace.FoldersChanged += OnWorkspaceFoldersChanged;
        RewatchFolders();
        if (_workspace.Folders.Count > 0) _ = DiscoverTestsAsync();
    }

    public void Dispose()
    {
        _workspace.RootChanged -= OnWorkspaceRootChanged;
        _workspace.FoldersChanged -= OnWorkspaceFoldersChanged;
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }

    private void RewatchFolders()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        foreach (var folder in _workspace.Folders)
        {
            var w = new TestSourceWatcher(_dispatcher, filters: _profile.WatchPatterns,
                ignoreDirs: _profile.WatchIgnoreDirs);
            w.Changed += () => _ = DiscoverTestsAsync();
            w.Watch(folder);
            _watchers.Add(w);
        }
    }

    /// <summary>一覧・各行の状態が変わったとき（UI スレッド）。エディタのガターのテストグリフ再送の契機。</summary>
    public event Action? TestsChanged;

    IReadOnlyList<TestItemViewModel> ITestExplorer.TestItems => Tests;

    /// <summary>ガターの ▶／コマンドパレットからの単体実行（ファイル限定＋1 件指定）。
    /// vitest / jest は <c>-t</c>、pytest はノード ID で 1 件だけ走らせられるので、dotnet 側と同じ動線をそのまま出せる。</summary>
    async Task<bool> ITestExplorer.RunTestAsync(TestItemViewModel test)
    {
        if (!RunSingleTestCommand.CanExecute(test)) return false;
        await RunSingleTestCommand.ExecuteAsync(test);
        return true;
    }

    /// <summary>テストタブが表示されたときの保険的な収集。</summary>
    public void EnsureTestsDiscovered()
    {
        if (Tests.Count == 0 && !IsDiscoveringTests) _ = DiscoverTestsAsync();
    }

    /// <summary>テスト行のダブルクリック：探索で拾ったソース位置（失敗していればトレースバックの位置）へジャンプする。</summary>
    public void NavigateToTestSource(TestItemViewModel? t)
    {
        if (t is { HasSource: true, SourcePath: { } p })
            _session.RaiseFrameActivated(p, t.Line - 1);  // 1始まり → エディタ 0始まり
    }

    private void OnWorkspaceRootChanged(object? sender, string? root)
    {
        Interlocked.Increment(ref _discoveryGeneration);
        _ = _dispatcher.InvokeAsync(() => { RewatchFolders(); _ = DiscoverTestsAsync(); },
            DispatcherPriority.Background);
    }

    private void OnWorkspaceFoldersChanged(object? sender, EventArgs e)
    {
        Interlocked.Increment(ref _discoveryGeneration);
        _ = _dispatcher.InvokeAsync(() => { RewatchFolders(); _ = DiscoverTestsAsync(); },
            DispatcherPriority.Background);
    }

    private async Task DiscoverTestsAsync()
    {
        if (_workspace.Folders.Count == 0) return;
        if (IsDiscoveringTests) { _rediscoverRequested = true; return; }

        IsDiscoveringTests = true;
        try
        {
            do
            {
                _rediscoverRequested = false;
                var generation = Volatile.Read(ref _discoveryGeneration);
                var folders = _workspace.Folders.ToArray();
                IReadOnlyList<ScriptDiscoveredTest> found;
                try { found = await Task.Run(() => DiscoverAll(folders)); }
                catch { found = Array.Empty<ScriptDiscoveredTest>(); }
                await _dispatcher.InvokeAsync(() =>
                {
                    if (generation == Volatile.Read(ref _discoveryGeneration))
                        ApplyDiscovered(found);
                    else
                        _rediscoverRequested = true;
                });
            } while (_rediscoverRequested);
        }
        finally { IsDiscoveringTests = false; }
    }

    /// <summary>全フォルダーを走査する（バックグラウンド）。<see cref="ScriptTestProfile.RequireProject"/> なら、
    /// ランナーのプロジェクトに属さないファイルのテストを落とす（判定はディレクトリ単位でキャッシュ）。</summary>
    private List<ScriptDiscoveredTest> DiscoverAll(IReadOnlyList<string> folders)
    {
        var found = folders.SelectMany(_profile.Discover).ToList();
        if (!_profile.RequireProject) return found;

        var owned = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        return found.Where(d =>
        {
            var dir = Path.GetDirectoryName(d.FilePath) ?? "";
            if (!owned.TryGetValue(dir, out var ok))
                owned[dir] = ok = TestProjectLocator.Find(d.FilePath, folders, _profile.Runners) is not null;
            return ok;
        }).ToList();
    }

    /// <summary>収集結果を既存の一覧へマージする（dotnet 版と同じ流儀：新規は追加、消えた未実行テストは除去、
    /// 結果を持つ行は残す）。位置（ファイル/行）は再収集のたびに追従する。</summary>
    internal void ApplyDiscovered(IReadOnlyList<ScriptDiscoveredTest> found)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existing = new Dictionary<string, TestItemViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Tests) existing[t.FullyQualifiedName] = t;

        foreach (var d in found)
        {
            var fqn = ScriptTestRunner.MakeFqn(d.FilePath, d.Title);
            keep.Add(fqn);
            if (!existing.TryGetValue(fqn, out var item))
            {
                item = CreateItem(d.FilePath, d.Title);
                Tests.Add(item);
                existing[fqn] = item;
            }
            item.IsParameterized = d.IsParameterized;
            item.SourcePath = d.FilePath;
            item.Line = d.Line1;
            // 宣言位置はエディタのガターの ▶ を置く場所（スクリプト系は探索の位置がそのまま宣言位置）。
            item.DeclarationPath = d.FilePath;
            item.DeclarationLine = d.Line1;
        }

        for (var i = Tests.Count - 1; i >= 0; i--)
        {
            var t = Tests[i];
            if (keep.Contains(t.FullyQualifiedName)) continue;
            if (t.Status != TestStatus.NotRun) continue;
            Tests.RemoveAt(i);
        }

        SyncTree();
        RecomputeSummary();
        TestsChanged?.Invoke();
    }

    /// <summary>1 テスト行を作る：グループキー＝正規化ファイルパス、葉表示＝タイトル、個別実行の指定＝
    /// JS は葉タイトル（<c>-t</c> 用。describe 前置は落とす）、pytest はノード ID 後半そのもの。</summary>
    private TestItemViewModel CreateItem(string filePath, string title)
    {
        string full;
        try { full = Path.GetFullPath(filePath); } catch { full = filePath; }
        var leaf = _profile.LeafOf(title);
        return new TestItemViewModel(
            fullyQualifiedName: ScriptTestRunner.MakeFqn(full, title),
            className: full,
            methodName: title,
            displayName: $"{Path.GetFileName(full)}: {leaf}",
            filterExpression: leaf);
    }

    /// <summary>ワークスペースの全テストを実行する。テストが複数プロジェクトにまたがる場合は
    /// プロジェクトごとに 1 回ずつ実行して結果をまとめる。</summary>
    [RelayCommand(CanExecute = nameof(CanRunTask))]
    private async Task Test()
    {
        if (Tests.Count == 0) { _session.Append(DebugOutputCategory.Important, "実行できるテストがありません。"); return; }
        _session.RequestOutput();  // 押下時に即「出力」へ
        _session.IsTaskRunning = true;
        try
        {
            _session.StatusMessage = "テスト実行中…";
            foreach (var t in Tests) t.SetRunning();
            TestsChanged?.Invoke();   // 実行中グリフ（…）をガターへすぐ出す
            var folders = _workspace.Folders.ToArray();
            var projects = new Dictionary<string, ITestRunner>(StringComparer.OrdinalIgnoreCase);
            var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Tests.Select(t => t.ClassName).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (TestProjectLocator.Find(file, folders, _profile.Runners) is { } p)
                    projects.TryAdd(p.ProjectDir, p.Runner);
                else
                    missing.Add(file);
            }
            if (missing.Count > 0)
                _session.Append(DebugOutputCategory.Important, missing.Count == 1
                    ? $"{_profile.MissingRunnerMessage}: {missing.First()}"
                    : $"{_profile.MissingRunnerMessage}（{missing.Count} ファイル）: {missing.First()} ほか");

            var hadResults = false;
            foreach (var (dir, runner) in projects)
                hadResults |= await RunProjectAsync(dir, runner, [],
                    $"テスト（{runner.Id}）: {Path.GetFileName(dir)}");
            _session.StatusMessage = CountStatus(hadResults, Tests);
        }
        finally
        {
            // 後始末は必ず通す（例外・中断で「実行中」のまま固まらせない。ガターのグリフも同じ状態を映す）。
            foreach (var t in Tests) if (t.Status == TestStatus.Running) t.ResetStatus();  // 未突合は戻す
            SyncTree();
            RecomputeSummary();
            TestsChanged?.Invoke();
            _session.IsTaskRunning = false;
        }
    }

    /// <summary>失敗したテストだけを再実行する。プロジェクトごとに 1 回、失敗したテストを並べて走らせる
    /// （pytest＝ノード ID の列、vitest / jest＝ファイルの列＋<c>-t</c> の選択。§28.14）。</summary>
    [RelayCommand(CanExecute = nameof(CanRerunFailed))]
    private async Task RerunFailed()
    {
        var failed = Tests.Where(t => t.Status == TestStatus.Failed).ToList();
        if (failed.Count == 0) return;
        _session.RequestOutput();
        _session.IsTaskRunning = true;
        try
        {
            _session.StatusMessage = "失敗テストを再実行中…";
            foreach (var t in failed) t.SetRunning();
            UpdateAggregates();
            TestsChanged?.Invoke();   // 実行中グリフ（…）をガターへすぐ出す

            var hadResults = false;
            var plans = PlanRerun(failed, _workspace.Folders.ToArray(), _profile.Runners, out var missing);
            foreach (var plan in plans)
                hadResults |= await RunProjectAsync(plan.ProjectDir, plan.Runner, plan.Targets,
                    $"失敗テストの再実行（{plan.Runner.Id}）: {Path.GetFileName(plan.ProjectDir)}");
            if (missing.Count > 0)
                _session.Append(DebugOutputCategory.Important, $"{_profile.MissingRunnerMessage}: {missing[0]}"
                    + (missing.Count > 1 ? $" ほか {missing.Count - 1} ファイル" : ""));
            _session.StatusMessage = CountStatus(hadResults, failed);
        }
        finally
        {
            foreach (var t in failed) if (t.Status == TestStatus.Running) t.ResetStatus();
            SyncTree();
            RecomputeSummary();
            TestsChanged?.Invoke();
            _session.IsTaskRunning = false;
        }
    }

    /// <summary>失敗の再実行の段取り（純関数。単体テストから直接呼ぶ）：テストをプロジェクトごとに束ね、
    /// プロジェクト相対のファイル＋1 件指定の列にする。ランナーを特定できないファイルは <paramref name="missing"/> へ。</summary>
    internal static IReadOnlyList<(string ProjectDir, ITestRunner Runner, IReadOnlyList<TestTarget> Targets)> PlanRerun(
        IEnumerable<TestItemViewModel> tests, IReadOnlyList<string> folders, IReadOnlyList<ITestRunner> runners,
        out List<string> missing)
    {
        missing = new List<string>();
        var plans = new List<(string ProjectDir, ITestRunner Runner, IReadOnlyList<TestTarget> Targets)>();
        var byDir = new Dictionary<string, List<TestTarget>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tests)
        {
            if (TestProjectLocator.Find(t.ClassName, folders, runners) is not { } project)
            {
                if (!missing.Contains(t.ClassName, StringComparer.OrdinalIgnoreCase)) missing.Add(t.ClassName);
                continue;
            }
            if (!byDir.TryGetValue(project.ProjectDir, out var targets))
            {
                byDir[project.ProjectDir] = targets = new List<TestTarget>();
                plans.Add((project.ProjectDir, project.Runner, targets));
            }
            var target = new TestTarget(Path.GetRelativePath(project.ProjectDir, t.ClassName),
                string.IsNullOrEmpty(t.FilterExpression) ? null : t.FilterExpression);
            if (!targets.Contains(target)) targets.Add(target);
        }
        return plans;
    }

    /// <summary>1 件のテストだけ実行する（ファイル限定＋1 件指定。JS の <c>-t</c> は同名タイトルが同ファイルに
    /// 複数あれば全部走る——ベストエフォート）。</summary>
    [RelayCommand(CanExecute = nameof(CanRunTask))]
    private async Task RunSingleTest(TestItemViewModel? item)
    {
        if (item is null) return;
        await RunScopedAsync(new[] { item }, item.ClassName, item.FilterExpression,
            $"テスト実行中… {item.DisplayName}", $"テスト: {item.DisplayName}",
            had => item.Status switch
            {
                TestStatus.Failed => "テスト失敗",
                TestStatus.Passed => "テスト成功",
                _ => had ? "テスト完了" : "テスト結果を取得できませんでした",
            });
    }

    /// <summary>ファイルグループ内のテストをまとめて実行する（ファイル限定実行）。</summary>
    [RelayCommand(CanExecute = nameof(CanRunTask))]
    private async Task RunGroup(TestGroupViewModel? group)
    {
        if (group is null) return;
        await RunScopedAsync(group.Tests.ToList(), group.Key, selector: null,
            $"テスト実行中… {group.Name}", $"テスト: {group.Name}",
            had => CountStatus(had, group.Tests));
    }

    /// <summary>共有テストビューのファイル単位実行。スクリプト系ではグループ自体がファイルなので、グループ実行へ委譲する。</summary>
    [RelayCommand(CanExecute = nameof(CanRunTask))]
    private Task RunFile(TestGroupViewModel? group) => RunGroup(group);

    // DebugTestsView は C# とスクリプト系で共有するため、こちらにも同名 Command を用意する。
    // vitest / jest はランナー本体を js-debug の pwa-node launch で起動する（C# の testhost attach とは別経路）。
    [RelayCommand(CanExecute = nameof(CanRunTask))]
    private Task DebugSingleTest(TestItemViewModel? item)
        => item is null ? Task.CompletedTask : DebugTestsAsync([item], $"テストデバッグ: {item.DisplayName}");

    [RelayCommand(CanExecute = nameof(CanRunTask))]
    private Task DebugGroup(TestGroupViewModel? group)
        => group is null ? Task.CompletedTask
            : DebugTestsAsync(Tests.Where(t => string.Equals(t.ClassName, group.Key, StringComparison.OrdinalIgnoreCase))
                .ToList(), $"テストデバッグ: {group.Name}", wholeFile: true);   // 絞り込みで隠れた行も同じファイルなら走る

    [RelayCommand(CanExecute = nameof(CanRunTask))]
    private Task DebugFile(TestGroupViewModel? group) => DebugGroup(group);

    /// <summary>テストをデバッガの下で走らせる。1 プロジェクトに限る（デバッガで起動するプロセスは 1 つ）。
    /// 終わったらレポートを通常実行と同じく一覧へ反映する。</summary>
    private async Task DebugTestsAsync(IReadOnlyList<TestItemViewModel> tests, string label, bool wholeFile = false)
    {
        if (tests.Count == 0) return;
        if (_debugLauncher is null)
        {
            _session.StatusMessage = $"{_profile.LanguageLabel}テストのデバッグは未対応です";
            return;
        }

        var plans = PlanRerun(tests, _workspace.Folders.ToArray(), _profile.Runners, out var missing);
        if (missing.Count > 0 || plans.Count == 0)
        {
            _session.Append(DebugOutputCategory.Important,
                $"{_profile.MissingRunnerMessage}: {(missing.Count > 0 ? missing[0] : tests[0].ClassName)}");
            return;
        }
        if (plans.Count > 1)
        {
            _session.StatusMessage = "テストデバッグは 1 つのパッケージ内のテストに限ります";
            _session.Append(DebugOutputCategory.Important,
                $"選んだテストが {plans.Count} つのパッケージにまたがっています。パッケージごとに分けてデバッグしてください。");
            return;
        }

        var (projectDir, runner, targets) = plans[0];
        if (runner is not ITestDebugLauncher debuggable)
        {
            _session.StatusMessage = $"{runner.Id} のテストデバッグは未対応です";
            return;
        }
        // ファイル単位のデバッグは -t で削らない（行の一覧に無い動的テストも走らせる）。
        if (wholeFile) targets = targets.Select(t => t.File).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(f => new TestTarget(f)).ToList();
        if (ScriptTestRunner.NewResultFile(runner, _session) is not { } resultFile) return;
        var tools = new TestToolLocator(_workspace.Folders.ToArray());
        if (debuggable.BuildDebugLaunch(new TestRunRequest(projectDir, resultFile, Targets: targets), tools) is not { } launch)
        {
            _session.StatusMessage = "テストデバッグを始められません";
            _session.Append(DebugOutputCategory.Important,
                $"node_modules に {runner.Id} が見つかりません（デバッグはランナー本体を node で直接起動します）。"
                + "パッケージの依存を導入してください（npm install）。");
            return;
        }

        foreach (var t in tests) t.SetRunning();
        UpdateAggregates();
        TestsChanged?.Invoke();
        IDebugService? service = null;
        try { service = await _debugLauncher(launch, label); }
        finally
        {
            if (service is null) FinishDebugRun(tests, runner, projectDir, resultFile);
        }
        if (service is null) return;

        void OnExited(object? sender, DebugExited e)
        {
            service.Exited -= OnExited;
            _dispatcher.BeginInvoke(new Action(() => FinishDebugRun(tests, runner, projectDir, resultFile)));
        }
        service.Exited += OnExited;
        // 起動が瞬時に失敗／終了した場合、購読より先に Exited が来ている（DebugTestsViewModel と同じ競合対策）。
        if (service.State is DebugSessionState.Idle or DebugSessionState.Terminated or DebugSessionState.Failed)
            OnExited(service, new DebugExited(null, "test debug ended"));
    }

    /// <summary>デバッグ実行の後始末：レポートがあれば反映し、無ければ（途中で止めた）実行中の表示だけ戻す。</summary>
    private void FinishDebugRun(IReadOnlyList<TestItemViewModel> tests, ITestRunner runner, string projectDir,
        string resultFile)
    {
        if (!tests.Any(t => t.Status == TestStatus.Running)) return;   // 二重の後始末（Exited と即時判定の両方）
        try
        {
            if (File.Exists(resultFile))
                ScriptTestRunner.ApplyResults(runner, resultFile, projectDir, _session, Tests, CreateItem);
        }
        finally
        {
            ScriptTestRunner.DeleteResults(resultFile);
            foreach (var t in tests) if (t.Status == TestStatus.Running) t.ResetStatus();
            SyncTree();
            RecomputeSummary();
            TestsChanged?.Invoke();
        }
    }

    /// <summary>ファイル限定実行の共通処理（1 件/グループ）。</summary>
    private async Task RunScopedAsync(IReadOnlyList<TestItemViewModel> running, string filePath, string? selector,
        string runningStatus, string label, Func<bool, string> finalStatus)
    {
        if (TestProjectLocator.Find(filePath, _workspace.Folders.ToArray(), _profile.Runners) is not { } project)
        {
            _session.Append(DebugOutputCategory.Important, $"{_profile.MissingRunnerMessage}: {filePath}");
            return;
        }

        _session.RequestOutput();
        _session.IsTaskRunning = true;
        try
        {
            _session.StatusMessage = runningStatus;
            foreach (var t in running) t.SetRunning();
            UpdateAggregates();
            TestsChanged?.Invoke();   // 実行中グリフ（…）をガターへすぐ出す
            var rel = Path.GetRelativePath(project.ProjectDir, filePath);
            var had = await RunProjectAsync(project.ProjectDir, project.Runner, [new TestTarget(rel, selector)], label);
            _session.StatusMessage = finalStatus(had);
        }
        finally
        {
            // 後始末は必ず通す（例外・中断で「実行中」のまま固まらせない。ガターのグリフも同じ状態を映す）。
            foreach (var t in running) if (t.Status == TestStatus.Running) t.ResetStatus();
            SyncTree();
            RecomputeSummary();
            TestsChanged?.Invoke();
            _session.IsTaskRunning = false;
        }
    }

    /// <summary>1 プロジェクト分の実行（コマンド組み立て → 可視ターミナル → レポート反映）。結果が得られたら true。</summary>
    private async Task<bool> RunProjectAsync(string projectDir, ITestRunner runner, IReadOnlyList<TestTarget> targets,
        string label)
    {
        var tools = new TestToolLocator(_workspace.Folders.ToArray());
        var report = await ScriptTestRunner.RunAsync(_terminal, _session, runner, projectDir, targets, tools,
            label);
        if (report is null) return false;
        try { return ScriptTestRunner.ApplyResults(runner, report, projectDir, _session, Tests, CreateItem); }
        finally { ScriptTestRunner.DeleteResults(report); }   // 実行ごとに一意名なので、読んだら消す
    }

    private static string CountStatus(bool hadResults, IEnumerable<TestItemViewModel> set)
    {
        if (!hadResults) return "テスト結果を取得できませんでした";
        var failed = set.Count(t => t.Status == TestStatus.Failed);
        return failed == 0 ? "テスト成功" : $"テスト失敗（{failed} 件）";
    }

    private bool CanRunTask() => !_session.IsBusy && !_session.IsTaskRunning;

    private bool CanRerunFailed() => CanRunTask() && FailedCount > 0;

    private void RecomputeSummary()
    {
        HasTestResults = Tests.Count > 0;
        FailedCount = Tests.Count(t => t.Status == TestStatus.Failed);
        RerunFailedCommand.NotifyCanExecuteChanged();
        if (!HasTestResults) { TestSummary = ""; return; }

        var passed = Tests.Count(t => t.Status == TestStatus.Passed);
        var failed = Tests.Count(t => t.Status == TestStatus.Failed);
        var skipped = Tests.Count(t => t.Status == TestStatus.Skipped);
        TestSummary = $"成功 {passed} / 失敗 {failed} / スキップ {skipped} / 合計 {Tests.Count}";
    }

    /// <summary>フラットな <see cref="Tests"/> をファイル単位のツリーへ再構築する。展開状態はキー（ファイルパス）で
    /// 引き継ぐ。グループ表示名はワークスペース相対パス。</summary>
    private void SyncTree()
    {
        var expanded = TestTree.ToDictionary(g => g.Key, g => g.IsExpanded, StringComparer.OrdinalIgnoreCase);
        TestTree.Clear();

        var filtering = !string.IsNullOrEmpty(TestFilter?.Trim())
            || !string.IsNullOrEmpty(TraitFilter?.Trim())
            || !(ShowPassed && ShowFailed && ShowNotRun);

        foreach (var g in Tests.GroupBy(t => t.ClassName, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var visible = g.Where(MatchesFilter).OrderBy(t => t.DeclarationLine).ToList();
            if (visible.Count == 0) continue;

            var node = new TestGroupViewModel(g.Key, _workspace.ToDisplayPath(g.Key), g.Key);
            foreach (var t in visible) node.Tests.Add(t);
            node.IsExpanded = filtering || (expanded.TryGetValue(g.Key, out var e) && e);
            node.RecomputeAggregate();
            TestTree.Add(node);
        }

        HasVisibleTests = TestTree.Count > 0;
    }

    private bool MatchesFilter(TestItemViewModel t)
    {
        var statusOk = t.Status switch
        {
            TestStatus.Passed => ShowPassed,
            TestStatus.Failed => ShowFailed,
            TestStatus.NotRun => ShowNotRun,
            TestStatus.Skipped => ShowNotRun,
            _ => true,
        };
        if (!statusOk) return false;

        var f = TestFilter?.Trim();
        if (!string.IsNullOrEmpty(f) &&
            !t.FullyQualifiedName.Contains(f, StringComparison.OrdinalIgnoreCase))
            return false;

        var traitFilter = TraitFilter?.Trim();
        return string.IsNullOrEmpty(traitFilter)
            || t.TraitsText.Contains(traitFilter, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateAggregates()
    {
        foreach (var g in TestTree) g.RecomputeAggregate();
    }
}
