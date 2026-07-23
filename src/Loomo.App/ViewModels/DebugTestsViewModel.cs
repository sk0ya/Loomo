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

/// <summary>テストエクスプローラのサブ ViewModel。ソース走査でテストを自動収集し、<c>dotnet test</c>（TRX）の結果で
/// 各行のステータスを更新する。全実行/クラス実行/個別実行と、状態・名前による絞り込みを持つ。</summary>
public sealed partial class DebugTestsViewModel : ObservableObject, ITestExplorer, IDisposable
{
    private readonly IWorkspaceService _workspace;
    private readonly ITerminalService _terminal;
    private readonly ITestDiscoveryService _testDiscovery;
    private readonly IDebugSession _session;
    private readonly Dispatcher _dispatcher;
    // ワークスペースフォルダーごとに1つ（複数フォルダー時は全フォルダーを監視する）。
    private readonly List<TestSourceWatcher> _watchers = new();

    /// <summary>探索中に来た再収集要求（探索完了後にもう一度走らせる）。</summary>
    private bool _rediscoverRequested;

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

    /// <summary>絞り込み：成功／失敗／未実施（探索だけ・スキップ）のテストを表示するか。</summary>
    [ObservableProperty] private bool _showPassed = true;
    [ObservableProperty] private bool _showFailed = true;
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
    partial void OnIsDiscoveringTestsChanged(bool value) => OnPropertyChanged(nameof(TestEmptyHint));

    /// <summary>テスト一覧（フラットな全件。突き合わせ・集計の元データ）。表示は <see cref="TestTree"/>。</summary>
    public ObservableCollection<TestItemViewModel> Tests { get; } = new();
    /// <summary>クラス単位にまとめたテストツリー（TreeView の表示元）。<see cref="SyncTree"/> で再構築する。</summary>
    public ObservableCollection<TestGroupViewModel> TestTree { get; } = new();

    internal DebugTestsViewModel(IWorkspaceService workspace, ITerminalService terminal,
        ITestDiscoveryService testDiscovery, IDebugSession session)
    {
        _workspace = workspace;
        _terminal = terminal;
        _testDiscovery = testDiscovery;
        _session = session;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _session.SessionStateChanged += () =>
        {
            TestCommand.NotifyCanExecuteChanged();
            RunSingleTestCommand.NotifyCanExecuteChanged();
            RunGroupCommand.NotifyCanExecuteChanged();
        };

        // テストはバックグラウンドで自動収集する。ワークスペースを開いた時点とソース変更を契機に高速探索で更新。
        // 複数フォルダー時は RootChanged／FoldersChanged のどちらでも全フォルダーを監視し直す。
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
            var w = new TestSourceWatcher(_dispatcher);
            w.Changed += () => _ = DiscoverTestsAsync();
            w.Watch(folder);
            _watchers.Add(w);
        }
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

    /// <summary>ソース走査でテスト一覧を収集する（ビルドを伴わない・バックグラウンド）。複数フォルダー時は
    /// 全フォルダーを走査して結果をマージする。探索中に来た要求は1回にまとめて末尾でもう一度回す
    /// （編集中の連続変更で重複起動しない）。</summary>
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
                IReadOnlyList<DiscoveredTest> found;
                try { found = await Task.Run(() => folders.SelectMany(_testDiscovery.Discover).ToList()); }
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

    /// <summary>1 件のテストだけ実行する（<c>--filter "FullyQualifiedName=..."</c>）。テオリは同メソッドの全ケースが対象。</summary>
    [RelayCommand(CanExecute = nameof(CanRunTask))]
    private async Task RunSingleTest(TestItemViewModel? item)
    {
        if (item is null) return;
        var target = _session.FindBuildTarget();
        if (target is null) return;
        await RunCoreAsync(target, new[] { item }, $"FullyQualifiedName={item.FilterExpression}",
            $"テスト実行中… {item.DisplayName}", $"テスト: {item.DisplayName}", UpdateAggregates, had => item.Status switch
            {
                TestStatus.Failed => "テスト失敗",
                TestStatus.Passed => "テスト成功",
                _ => had ? "テスト完了" : "テスト結果を取得できませんでした",
            });
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

    /// <summary>テスト実行の共通処理：対象行を実行中にし、<c>dotnet test</c>→TRX 反映→未突合の戻し→ツリー/集計更新→状態文言。</summary>
    private async Task RunCoreAsync(string target, IReadOnlyList<TestItemViewModel> running, string? filter,
        string runningStatus, string label, Action? prepare, Func<bool, string> finalStatus)
    {
        _session.IsTaskRunning = true;
        try
        {
            _session.StatusMessage = runningStatus;
            foreach (var t in running) t.SetRunning();
            prepare?.Invoke();
            var trx = await DotnetTestRunner.RunAsync(_terminal, _session, target, filter, label);
            if (trx is not null) DotnetTestRunner.ApplyTrx(trx, _session, Tests);
            foreach (var t in running) if (t.Status == TestStatus.Running) t.ResetStatus();  // 未突合は戻す
            SyncTree();
            RecomputeSummary();
            _session.StatusMessage = finalStatus(trx is not null);
        }
        finally { _session.IsTaskRunning = false; }
    }

    private static string CountStatus(bool hadResults, IEnumerable<TestItemViewModel> set)
    {
        if (!hadResults) return "テスト結果を取得できませんでした";
        var failed = set.Count(t => t.Status == TestStatus.Failed);
        return failed == 0 ? "テスト成功" : $"テスト失敗（{failed} 件）";
    }

    private bool CanRunTask() => !_session.IsBusy && !_session.IsTaskRunning;

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

            var name = g.Key.Length == 0 ? "(その他)" : g.Key[(g.Key.LastIndexOf('.') + 1)..];
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
}
