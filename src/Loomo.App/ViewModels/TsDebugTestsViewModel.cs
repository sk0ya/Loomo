using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>TS テストエクスプローラのサブ ViewModel（<see cref="DebugTestsViewModel"/> の TS 版）。
/// ソース走査（<see cref="TsTestDiscovery"/>）で vitest / jest のテストを自動収集し、JSON 結果
/// （<see cref="TsTestRunner"/>）で各行のステータスを更新する。公開メンバー名は dotnet 版と揃えてあり、
/// ビュー（DebugTestsView）をそのまま共有する。グループ＝テストファイル、葉＝テストタイトル。</summary>
public sealed partial class TsDebugTestsViewModel : ObservableObject, ITestExplorer, IDisposable
{
    private readonly IWorkspaceService _workspace;
    private readonly ITerminalService _terminal;
    private readonly IDebugSession _session;
    private readonly Dispatcher _dispatcher;
    private readonly List<TestSourceWatcher> _watchers = new();

    /// <summary>探索中に来た再収集要求（探索完了後にもう一度走らせる）。</summary>
    private bool _rediscoverRequested;

    [ObservableProperty] private string _testSummary = "";
    [ObservableProperty] private bool _hasTestResults;
    [ObservableProperty] private bool _isDiscoveringTests;

    public string TestEmptyHint => IsDiscoveringTests
        ? "テストを収集しています…"
        : "テストが見つかりませんでした（*.test.ts / *.spec.ts 等。ソース変更で自動収集します）。";

    [ObservableProperty] private bool _showPassed = true;
    [ObservableProperty] private bool _showFailed = true;
    [ObservableProperty] private bool _showNotRun = true;

    [ObservableProperty] private string _testFilter = "";

    [ObservableProperty] private bool _hasVisibleTests;

    public bool NoFilterMatch => HasTestResults && !HasVisibleTests;

    partial void OnShowPassedChanged(bool value) => SyncTree();
    partial void OnShowFailedChanged(bool value) => SyncTree();
    partial void OnShowNotRunChanged(bool value) => SyncTree();
    partial void OnTestFilterChanged(string value) => SyncTree();
    partial void OnHasVisibleTestsChanged(bool value) => OnPropertyChanged(nameof(NoFilterMatch));
    partial void OnHasTestResultsChanged(bool value) => OnPropertyChanged(nameof(NoFilterMatch));
    partial void OnIsDiscoveringTestsChanged(bool value) => OnPropertyChanged(nameof(TestEmptyHint));

    /// <summary>テスト一覧（フラットな全件）。FQN＝正規化ファイルパス::タイトル。</summary>
    public ObservableCollection<TestItemViewModel> Tests { get; } = new();
    /// <summary>ファイル単位にまとめたテストツリー（TreeView の表示元）。</summary>
    public ObservableCollection<TestGroupViewModel> TestTree { get; } = new();

    internal TsDebugTestsViewModel(IWorkspaceService workspace, ITerminalService terminal, IDebugSession session)
    {
        _workspace = workspace;
        _terminal = terminal;
        _session = session;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _session.SessionStateChanged += () =>
        {
            TestCommand.NotifyCanExecuteChanged();
            RunSingleTestCommand.NotifyCanExecuteChanged();
            RunGroupCommand.NotifyCanExecuteChanged();
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
            var w = new TestSourceWatcher(_dispatcher,
                filters: TsTestDiscovery.TestFilePatterns,
                ignoreDirs: ["node_modules", "dist", "build", "coverage"]);
            w.Changed += () => _ = DiscoverTestsAsync();
            w.Watch(folder);
            _watchers.Add(w);
        }
    }

    /// <summary>テストタブが表示されたときの保険的な収集。</summary>
    public void EnsureTestsDiscovered()
    {
        if (Tests.Count == 0 && !IsDiscoveringTests) _ = DiscoverTestsAsync();
    }

    /// <summary>テスト行のダブルクリック：探索で拾ったソース位置へジャンプする。</summary>
    public void NavigateToTestSource(TestItemViewModel? t)
    {
        if (t is { HasSource: true, SourcePath: { } p })
            _session.RaiseFrameActivated(p, t.Line - 1);  // 1始まり → エディタ 0始まり
    }

    private void OnWorkspaceRootChanged(object? sender, string? root)
        => _dispatcher.InvokeAsync(() => { RewatchFolders(); _ = DiscoverTestsAsync(); },
            DispatcherPriority.Background);

    private void OnWorkspaceFoldersChanged(object? sender, EventArgs e)
        => _dispatcher.InvokeAsync(() => { RewatchFolders(); _ = DiscoverTestsAsync(); },
            DispatcherPriority.Background);

    private async Task DiscoverTestsAsync()
    {
        var folders = _workspace.Folders;
        if (folders.Count == 0) return;
        if (IsDiscoveringTests) { _rediscoverRequested = true; return; }

        IsDiscoveringTests = true;
        try
        {
            do
            {
                _rediscoverRequested = false;
                IReadOnlyList<TsTestDiscovery.TsDiscoveredTest> found;
                try { found = await Task.Run(() => folders.SelectMany(TsTestDiscovery.Discover).ToList()); }
                catch { found = Array.Empty<TsTestDiscovery.TsDiscoveredTest>(); }
                await _dispatcher.InvokeAsync(() => ApplyDiscovered(found));
            } while (_rediscoverRequested);
        }
        finally { IsDiscoveringTests = false; }
    }

    /// <summary>収集結果を既存の一覧へマージする（dotnet 版と同じ流儀：新規は追加、消えた未実行テストは除去、
    /// 結果を持つ行は残す）。位置（ファイル/行）は再収集のたびに追従する。</summary>
    private void ApplyDiscovered(IReadOnlyList<TsTestDiscovery.TsDiscoveredTest> found)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existing = new Dictionary<string, TestItemViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Tests) existing[t.FullyQualifiedName] = t;

        foreach (var d in found)
        {
            var fqn = TsTestRunner.MakeFqn(d.FilePath, d.Title);
            keep.Add(fqn);
            if (existing.TryGetValue(fqn, out var item))
            {
                item.IsParameterized = d.IsEach;
                item.SourcePath = d.FilePath;
                item.Line = d.Line1;
            }
            else
            {
                var created = CreateItem(d.FilePath, d.Title);
                created.IsParameterized = d.IsEach;
                created.SourcePath = d.FilePath;
                created.Line = d.Line1;
                Tests.Add(created);
            }
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
    }

    /// <summary>1 テスト行を TS 規約で作る：グループキー＝正規化ファイルパス、葉表示＝タイトル、
    /// 個別実行フィルタ＝葉タイトル（-t 用。describe 前置は落とす）。</summary>
    private static TestItemViewModel CreateItem(string filePath, string title)
    {
        string full;
        try { full = Path.GetFullPath(filePath); } catch { full = filePath; }
        var lastSep = title.LastIndexOf(" > ", StringComparison.Ordinal);
        var leaf = lastSep >= 0 ? title[(lastSep + 3)..] : title;
        return new TestItemViewModel(
            fullyQualifiedName: TsTestRunner.MakeFqn(full, title),
            className: full,
            methodName: title,
            displayName: $"{Path.GetFileName(full)}: {leaf}",
            filterExpression: leaf);
    }

    /// <summary>ワークスペースの全テストを実行する。テストが複数パッケージにまたがる場合は
    /// パッケージ（最寄りの package.json）ごとに 1 回ずつ実行して結果をまとめる。</summary>
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
            var hadResults = false;
            foreach (var pkgDir in Tests.Select(t => FindPackageDir(t.ClassName)).Where(d => d is not null)
                         .Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                hadResults |= await RunPackageAsync(pkgDir!, fileScope: null, testName: null,
                    $"テスト: {Path.GetFileName(pkgDir!)}");
            foreach (var t in Tests) if (t.Status == TestStatus.Running) t.ResetStatus();  // 未突合は戻す
            SyncTree();
            RecomputeSummary();
            _session.StatusMessage = CountStatus(hadResults, Tests);
        }
        finally { _session.IsTaskRunning = false; }
    }

    /// <summary>1 件のテストだけ実行する（ファイル限定＋<c>-t 葉タイトル</c>。同名タイトルが同ファイルに
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
        await RunScopedAsync(group.Tests.ToList(), group.Key, testName: null,
            $"テスト実行中… {group.Name}", $"テスト: {group.Name}",
            had => CountStatus(had, group.Tests));
    }

    /// <summary>ファイル限定実行の共通処理（1 件/グループ）。</summary>
    private async Task RunScopedAsync(IReadOnlyList<TestItemViewModel> running, string filePath, string? testName,
        string runningStatus, string label, Func<bool, string> finalStatus)
    {
        var pkgDir = FindPackageDir(filePath);
        if (pkgDir is null)
        {
            _session.Append(DebugOutputCategory.Important, $"package.json が見つかりません: {filePath}");
            return;
        }

        _session.RequestOutput();
        _session.IsTaskRunning = true;
        try
        {
            _session.StatusMessage = runningStatus;
            foreach (var t in running) t.SetRunning();
            UpdateAggregates();
            var rel = Path.GetRelativePath(pkgDir, filePath);
            var had = await RunPackageAsync(pkgDir, rel, testName, label);
            foreach (var t in running) if (t.Status == TestStatus.Running) t.ResetStatus();
            SyncTree();
            RecomputeSummary();
            _session.StatusMessage = finalStatus(had);
        }
        finally { _session.IsTaskRunning = false; }
    }

    /// <summary>1 パッケージ分の実行（ランナー判定 → npx → JSON 反映）。結果が得られたら true。</summary>
    private async Task<bool> RunPackageAsync(string pkgDir, string? fileScope, string? testName, string label)
    {
        var pkgJson = Path.Combine(pkgDir, "package.json");
        var runner = TsTestRunner.DetectRunner(pkgJson);
        if (runner is null)
        {
            _session.Append(DebugOutputCategory.Important,
                $"テストランナー（vitest / jest）を package.json から特定できません: {pkgJson}");
            return false;
        }

        var json = await TsTestRunner.RunAsync(_terminal, _session, runner, pkgDir, fileScope, testName, label);
        if (json is null) return false;
        TsTestRunner.ApplyResults(json, _session, Tests, CreateItem);
        return true;
    }

    /// <summary>テストファイルの最寄りの package.json のディレクトリ（ファイルのディレクトリから最大 6 段上る）。</summary>
    private static string? FindPackageDir(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        for (var i = 0; i < 6 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "package.json")))
                return dir;
        return null;
    }

    private static string CountStatus(bool hadResults, IEnumerable<TestItemViewModel> set)
    {
        if (!hadResults) return "テスト結果を取得できませんでした";
        var failed = set.Count(t => t.Status == TestStatus.Failed);
        return failed == 0 ? "テスト成功" : $"テスト失敗（{failed} 件）";
    }

    private bool CanRunTask() => !_session.IsBusy && !_session.IsTaskRunning;

    private void RecomputeSummary()
    {
        HasTestResults = Tests.Count > 0;
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
            || !(ShowPassed && ShowFailed && ShowNotRun);

        foreach (var g in Tests.GroupBy(t => t.ClassName, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var visible = g.Where(MatchesFilter).OrderBy(t => t.Line).ToList();
            if (visible.Count == 0) continue;

            var node = new TestGroupViewModel(g.Key, ToRelativeDisplay(g.Key));
            foreach (var t in visible) node.Tests.Add(t);
            node.IsExpanded = filtering || (expanded.TryGetValue(g.Key, out var e) && e);
            node.RecomputeAggregate();
            TestTree.Add(node);
        }

        HasVisibleTests = TestTree.Count > 0;
    }

    /// <summary>グループ見出し用のワークスペース相対パス（SEARCH/問題タブと同じく / 区切り）。外なら絶対のまま。</summary>
    private string ToRelativeDisplay(string filePath)
    {
        foreach (var folder in _workspace.Folders)
        {
            var rel = Path.GetRelativePath(folder, filePath);
            if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
                return rel.Replace('\\', '/');
        }
        return filePath;
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
        return string.IsNullOrEmpty(f)
            || t.FullyQualifiedName.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateAggregates()
    {
        foreach (var g in TestTree) g.RecomputeAggregate();
    }
}
