using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Harness;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>エディタで開いてほしいファイルと行（1始まり、0 なら先頭）。</summary>
public sealed record HarnessOpenRequest(string Path, int Line);

/// <summary>
/// ハーネスペイン（<see cref="PaneKind.Harness"/>、§37）。ハーネスの改善箇所を見つけ、根拠を辿り、版を比べる作業台。
/// <para><b>観測事実と推測を混ぜない</b>：数値は記録にあるものだけ（料金が無ければ「不明」）、推定トークンと
/// 「関連しそうな構成要素」「改善の方向」は推測として書く（§37.2）。</para>
/// <para>記録の解析は重いので、初めて見えたとき・期間を変えたとき・更新を押したときだけ走る。記録フォルダーは
/// 監視しない——Claude Code はターンごとに追記するので、監視すると作業中ずっと再解析が走る（§37.8）。</para>
/// </summary>
public sealed partial class HarnessPaneViewModel : ObservableObject
{
    public const int TabCandidates = 0;
    public const int TabRuns = 1;
    public const int TabConfig = 2;
    public const int TabVersions = 3;

    private const int DetailMaxChars = 20_000;

    private readonly HarnessAnalyzer _analyzer;
    private readonly IWorkspaceService _workspace;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _detailCts;
    private HarnessSnapshot? _snapshot;
    private bool _loaded;
    private bool _stale = true;

    public HarnessPaneViewModel(HarnessAnalyzer analyzer, IWorkspaceService workspace)
    {
        _analyzer = analyzer;
        _workspace = workspace;
        // ワークスペースが変われば記録の出どころも変わる。見えていれば読み直し、見えていなければ次に見えたとき。
        _workspace.FoldersChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _stale = true;
            if (_loaded)
                _ = RefreshAsync();
        });
    }

    public IReadOnlyList<HarnessPeriodOption> PeriodOptions { get; } =
    [
        new(7, "7日"), new(30, "30日"), new(90, "90日"), new(365, "1年"),
    ];

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private HarnessPeriodOption? _selectedPeriod;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _emptyMessage = "";

    // ===== 頭の帯（周辺視野） =====
    [ObservableProperty] private string _runsLabel = "—";
    [ObservableProperty] private string _verdictsLabel = "—";
    [ObservableProperty] private string _tokensPerAcceptedLabel = "—";
    [ObservableProperty] private string _totalTokensLabel = "—";
    [ObservableProperty] private string _cacheLabel = "—";
    [ObservableProperty] private string _errorRateLabel = "—";
    [ObservableProperty] private string _costLabel = "—";
    [ObservableProperty] private string _harnessLabel = "";

    // ===== 改善候補 =====
    public ObservableCollection<HarnessComponentRow> Components { get; } = new();
    public ObservableCollection<HarnessCandidateRow> Candidates { get; } = new();
    public ObservableCollection<HarnessEvidenceRow> Evidence { get; } = new();
    [ObservableProperty] private HarnessComponentRow? _selectedComponent;
    /// <summary>1 Run でしか起きていない候補を隠す（1回きりは偶然のことが多い）。</summary>
    [ObservableProperty] private bool _hideSingleRunCandidates;
    [ObservableProperty] private HarnessCandidateRow? _selectedCandidate;
    [ObservableProperty] private HarnessEvidenceRow? _selectedEvidence;
    [ObservableProperty] private string _candidateObservation = "";
    [ObservableProperty] private string _candidateDirection = "";
    [ObservableProperty] private string _candidateRelated = "";
    [ObservableProperty] private string _evidenceDetail = "";

    // ===== 実行 =====
    public ObservableCollection<HarnessRunRow> Runs { get; } = new();
    public ObservableCollection<HarnessCallRow> RunCalls { get; } = new();
    [ObservableProperty] private HarnessRunRow? _selectedRun;
    [ObservableProperty] private HarnessCallRow? _selectedCall;
    [ObservableProperty] private string _runDetail = "";
    [ObservableProperty] private string _callDetail = "";

    // ===== 構成・版 =====
    public ObservableCollection<HarnessConfigRow> ConfigRows { get; } = new();
    public ObservableCollection<HarnessVersionRowViewModel> Versions { get; } = new();
    [ObservableProperty] private HarnessConfigRow? _selectedConfig;
    [ObservableProperty] private string _configNote = "";

    /// <summary>エディタで開く（ShellWindow が受ける）。</summary>
    public event EventHandler<HarnessOpenRequest>? OpenRequested;
    /// <summary>タブ・期間が変わった（ワークスペースへ保存する合図）。</summary>
    public event EventHandler? StateChanged;

    public bool HasSelectedCandidate => SelectedCandidate is not null;
    public bool HasSelectedRun => SelectedRun is not null;

    /// <summary>ペインが初めて見えたとき（またはワークスペースが変わってから最初に見えたとき）に読む。</summary>
    public void EnsureLoaded()
    {
        _loaded = true;
        if (_stale)
            _ = RefreshAsync();
    }

    public HarnessPaneSnapshot Capture() => new()
    {
        SelectedTab = SelectedTab,
        PeriodDays = (SelectedPeriod ?? DefaultPeriod).Days,
        HideSingleRunCandidates = HideSingleRunCandidates,
    };

    public void Restore(HarnessPaneSnapshot? snapshot)
    {
        _restoring = true;
        try
        {
            SelectedTab = snapshot is { SelectedTab: >= TabCandidates and <= TabVersions } s ? s.SelectedTab : TabCandidates;
            SelectedPeriod = PeriodOptions.FirstOrDefault(p => p.Days == snapshot?.PeriodDays) ?? DefaultPeriod;
            HideSingleRunCandidates = snapshot?.HideSingleRunCandidates ?? false;
        }
        finally
        {
            _restoring = false;
        }
        _stale = true;
        if (_loaded)
            _ = RefreshAsync();
    }

    private bool _restoring;

    private HarnessPeriodOption DefaultPeriod => PeriodOptions[1];

    partial void OnSelectedTabChanged(int value)
    {
        if (!_restoring)
            StateChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedPeriodChanged(HarnessPeriodOption? value)
    {
        if (_restoring)
            return;
        StateChanged?.Invoke(this, EventArgs.Empty);
        if (_loaded)
            _ = RefreshAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        _stale = false;
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        IsLoading = true;
        StatusText = "記録を読んでいます…";
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            if (!cts.IsCancellationRequested && p.Total > 0)
                StatusText = $"記録を読んでいます… {p.Done}/{p.Total}";
        });
        try
        {
            var period = TimeSpan.FromDays((SelectedPeriod ?? DefaultPeriod).Days);
            var snapshot = await _analyzer.AnalyzeAsync(period, progress, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            Apply(snapshot);
            StatusText = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = $"読めませんでした: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
                IsLoading = false;
        }
    }

    private void Apply(HarnessSnapshot snapshot)
    {
        _snapshot = snapshot;
        var summary = HarnessMetrics.Summarize(snapshot.Runs, snapshot.Verdicts);
        RunsLabel = summary.RunCount.ToString();
        VerdictsLabel = $"{summary.Accepted} ／ {summary.Rejected}";
        TokensPerAcceptedLabel = summary.TokensPerAccepted is { } tpa ? HarnessFormat.Tokens(tpa) : "受け入れなし";
        TotalTokensLabel = HarnessFormat.Tokens(summary.Usage.Total);
        CacheLabel = HarnessFormat.Percent(summary.Usage.CacheReadRatio);
        ErrorRateLabel = HarnessFormat.Percent(summary.ToolErrorRate);
        CostLabel = summary.CostKnownRuns == 0
            ? "不明"
            : $"${summary.CostTotal:0.00}（記録 {summary.CostKnownRuns}/{summary.RunCount} Run）";
        var def = snapshot.Definition;
        HarnessLabel = def.HasHarnessFolder
            ? $"{def.HarnessId ?? ".harness"}{(def.DeclaredVersion is { } v ? " " + v : "")}"
            : ".harness なし";

        EmptyMessage = snapshot.TranscriptDirectories.Count == 0
            ? "このワークスペースで起動した Claude Code の記録が見つかりません（~/.claude/projects）。"
            : snapshot.Runs.Count == 0 ? "この期間の記録がありません。期間を広げてください。" : "";

        var runsById = snapshot.Runs.ToDictionary(r => r.Id, StringComparer.Ordinal);
        ApplyComponents(snapshot, runsById);
        ApplyRuns(snapshot);
        ApplyVersions(snapshot);
    }

    // ===== 改善候補タブ =====

    private void ApplyComponents(HarnessSnapshot snapshot, Dictionary<string, HarnessRun> runsById)
    {
        var previous = SelectedComponent?.Component?.Id;
        var previousCandidate = SelectedCandidate?.Candidate.Id;
        Components.Clear();
        Components.Add(new HarnessComponentRow(null, "", snapshot.Candidates.Count));
        ConfigRows.Clear();
        foreach (var component in snapshot.Definition.Components)
        {
            var usage = UsageOf(component, snapshot.Runs);
            var related = snapshot.Candidates.Count(c => IsRelated(c, component, runsById));
            Components.Add(new HarnessComponentRow(component, usage, related));
            ConfigRows.Add(new HarnessConfigRow(component, usage, DisplayPath(component.Path)));
        }
        ConfigNote = snapshot.Definition.HasHarnessFolder
            ? $"定義（設定されているもの）と、期間内の使用（観測）。.harness: {string.Join(", ", snapshot.Definition.HarnessFolders.Select(DisplayPath))}"
            : "定義（設定されているもの）と、期間内の使用（観測）。.harness/ はまだ無い——判定を付けるか、改善候補をタスクにすると作られる。";
        SelectedComponent = Components.FirstOrDefault(c => c.Component?.Id == previous) ?? Components[0];
        if (previousCandidate is not null)
            SelectedCandidate = Candidates.FirstOrDefault(c => c.Candidate.Id == previousCandidate) ?? SelectedCandidate;
    }

    /// <summary>構成要素の使用（観測）。Skill／サブエージェントだけは呼ばれた回数が記録に残る。</summary>
    private static string UsageOf(HarnessComponent component, IReadOnlyList<HarnessRun> runs) => component.Kind switch
    {
        HarnessComponentKind.Skill => CountLabel(runs.Sum(r => r.SkillsUsed.Count(s => s.Equals(component.Name, StringComparison.OrdinalIgnoreCase)))),
        HarnessComponentKind.Agent when component.Path.Contains(Path.DirectorySeparatorChar + ".claude" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            => CountLabel(runs.Sum(r => r.AgentsUsed.Count(a => a.Equals(component.Name, StringComparison.OrdinalIgnoreCase)))),
        HarnessComponentKind.Instructions => "毎回",
        _ => "—",
    };

    private static string CountLabel(int count) => count == 0 ? "観測なし" : $"{count}回";

    /// <summary>候補とこの構成要素が「関連しそう」か（推測）：規則の当たりの種類か、根拠の Run でその Skill が使われた。</summary>
    private static bool IsRelated(HarnessCandidate candidate, HarnessComponent component, Dictionary<string, HarnessRun> runsById)
    {
        if (candidate.RelatedKinds.Contains(component.Kind))
            return true;
        if (component.Kind != HarnessComponentKind.Skill)
            return false;
        return candidate.Evidence.Any(e => runsById.TryGetValue(e.RunId, out var run)
            && run.SkillsUsed.Any(s => s.Equals(component.Name, StringComparison.OrdinalIgnoreCase)));
    }

    partial void OnHideSingleRunCandidatesChanged(bool value)
    {
        if (!_restoring)
            StateChanged?.Invoke(this, EventArgs.Empty);
        OnSelectedComponentChanged(SelectedComponent);
    }

    partial void OnSelectedComponentChanged(HarnessComponentRow? value)
    {
        if (_snapshot is null)
            return;
        var runsById = _snapshot.Runs.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var previous = SelectedCandidate?.Candidate.Id;
        Candidates.Clear();
        foreach (var candidate in _snapshot.Candidates)
        {
            if (value?.Component is { } component && !IsRelated(candidate, component, runsById))
                continue;
            if (HideSingleRunCandidates && candidate.RunCount < 2)
                continue;
            Candidates.Add(new HarnessCandidateRow(candidate));
        }
        SelectedCandidate = Candidates.FirstOrDefault(c => c.Candidate.Id == previous) ?? Candidates.FirstOrDefault();
    }

    partial void OnSelectedCandidateChanged(HarnessCandidateRow? value)
    {
        OnPropertyChanged(nameof(HasSelectedCandidate));
        Evidence.Clear();
        if (value is null)
        {
            CandidateObservation = CandidateDirection = CandidateRelated = EvidenceDetail = "";
            return;
        }
        var c = value.Candidate;
        CandidateObservation = $"{c.Observation}  —  {c.RunCount} Run・根拠 {c.Evidence.Count} 件"
            + (c.EstimatedTokens > 0 ? $"・推定 {HarnessFormat.Tokens(c.EstimatedTokens)} トークン（文字数÷{HarnessDetectors.CharsPerToken}）" : "");
        CandidateDirection = c.Direction;
        var present = _snapshot?.Definition.Components.Where(comp => c.RelatedKinds.Contains(comp.Kind)).Select(comp => comp.Name).ToList() ?? [];
        CandidateRelated = string.Join(" ・ ", c.RelatedKinds.Select(HarnessFormat.Kind))
            + (present.Count > 0 ? $"（{string.Join(", ", present)}）" : "（該当する定義なし）");
        foreach (var e in c.Evidence)
            Evidence.Add(new HarnessEvidenceRow(e, e.FilePath is { } p ? DisplayPath(p) : null));
        SelectedEvidence = Evidence.FirstOrDefault();
    }

    partial void OnSelectedEvidenceChanged(HarnessEvidenceRow? value)
        => _ = LoadRecordAsync(value?.Evidence.TranscriptPath, value?.Evidence.LineNumber ?? 0, text => EvidenceDetail = text);

    [RelayCommand]
    private void OpenEvidenceRecord(HarnessEvidenceRow? row)
    {
        if ((row ?? SelectedEvidence) is { } r)
            OpenRequested?.Invoke(this, new HarnessOpenRequest(r.Evidence.TranscriptPath, r.Evidence.LineNumber));
    }

    [RelayCommand]
    private void OpenEvidenceFile(HarnessEvidenceRow? row)
    {
        if ((row ?? SelectedEvidence)?.Evidence.FilePath is { } path && File.Exists(path))
            OpenRequested?.Invoke(this, new HarnessOpenRequest(path, 0));
    }

    /// <summary>改善候補を <c>.harness/tasks/</c> のタスクとして書き出し、エディタで開く（構想§5）。</summary>
    [RelayCommand]
    private void CreateTask()
    {
        if (SelectedCandidate?.Candidate is not { } candidate)
            return;
        // 根拠の Run を起動したフォルダーのハーネスへ書く（マルチルート）。分からなければプライマリ。
        var runCwd = candidate.Evidence.Select(e => _snapshot?.Runs.FirstOrDefault(r => r.Id == e.RunId)?.Cwd).FirstOrDefault(c => c is not null);
        if (_workspace.FolderForOrPrimary(runCwd) is not { } folder)
        {
            StatusText = "ワークスペースフォルダーが開かれていません。";
            return;
        }
        try
        {
            var path = HarnessTaskWriter.Write(folder, candidate, DateTimeOffset.Now, DisplayPath);
            StatusText = $"タスクにしました: {DisplayPath(path)}";
            OpenRequested?.Invoke(this, new HarnessOpenRequest(path, 0));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"書き出せませんでした: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CopyCandidate()
    {
        if (SelectedCandidate?.Candidate is not { } candidate)
            return;
        try
        {
            Clipboard.SetText(HarnessTaskWriter.ToMarkdown(candidate, DateTimeOffset.Now, DisplayPath));
            StatusText = "改善候補を Markdown でコピーしました。";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            StatusText = "クリップボードが使えませんでした。";
        }
    }

    // ===== 実行タブ =====

    private void ApplyRuns(HarnessSnapshot snapshot)
    {
        var previous = SelectedRun?.Run.Id;
        Runs.Clear();
        foreach (var run in snapshot.Runs)
        {
            snapshot.Verdicts.TryGetValue(run.Id, out var verdict);
            Runs.Add(new HarnessRunRow(run, snapshot.VersionOf(run), verdict, HarnessTaskLinks.For(run, snapshot.Definition.Tasks)));
        }
        SelectedRun = Runs.FirstOrDefault(r => r.Run.Id == previous) ?? Runs.FirstOrDefault();
    }

    partial void OnSelectedRunChanged(HarnessRunRow? value)
    {
        OnPropertyChanged(nameof(HasSelectedRun));
        RunCalls.Clear();
        RunDetail = value?.Detail ?? "";
        if (value is null)
            return;
        foreach (var call in value.Run.ToolCalls)
            RunCalls.Add(new HarnessCallRow(call));
        CallDetail = "";
    }

    partial void OnSelectedCallChanged(HarnessCallRow? value)
        => _ = LoadRecordAsync(value?.Call.TranscriptPath, value?.Call.LineNumber ?? 0, text => CallDetail = text);

    [RelayCommand]
    private void OpenRunRecord()
    {
        if (SelectedCall is { } call)
            OpenRequested?.Invoke(this, new HarnessOpenRequest(call.Call.TranscriptPath, call.Call.LineNumber));
        else if (SelectedRun is { } run)
            OpenRequested?.Invoke(this, new HarnessOpenRequest(run.Run.TranscriptPath, 0));
    }

    [RelayCommand]
    private void AcceptRun() => SetVerdict(HarnessVerdictKind.Accepted);

    [RelayCommand]
    private void RejectRun() => SetVerdict(HarnessVerdictKind.Rejected);

    [RelayCommand]
    private void ClearVerdict() => SetVerdict(null);

    private void SetVerdict(HarnessVerdictKind? kind)
    {
        if (SelectedRun?.Run is not { } run || _snapshot is null)
            return;
        if (_workspace.FolderForOrPrimary(run.Cwd) is not { } folder)
        {
            StatusText = "ワークスペースフォルダーが開かれていません。";
            return;
        }
        var verdict = kind is { } k ? new HarnessVerdict(k, DateTimeOffset.Now, null) : null;
        try
        {
            HarnessVerdictStore.Set(HarnessVerdictStore.PathFor(folder), run.Id, verdict);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"判定を保存できませんでした: {ex.Message}";
            return;
        }
        // 判定は指標（受け入れ1件あたり・版の比較）にだけ効くので、記録を読み直さずに差し替える。
        var verdicts = new Dictionary<string, HarnessVerdict>(_snapshot.Verdicts, StringComparer.Ordinal);
        if (verdict is null) verdicts.Remove(run.Id);
        else verdicts[run.Id] = verdict;
        Apply(_snapshot with { Verdicts = verdicts });
    }

    // ===== 構成・版タブ =====

    [RelayCommand]
    private void OpenConfig(HarnessConfigRow? row)
    {
        if ((row ?? SelectedConfig)?.Component.Path is { } path && File.Exists(path))
            OpenRequested?.Invoke(this, new HarnessOpenRequest(path, 0));
    }

    private void ApplyVersions(HarnessSnapshot snapshot)
    {
        Versions.Clear();
        foreach (var row in HarnessMetrics.CompareVersions(snapshot.Runs, snapshot.Verdicts, snapshot.VersionOf, snapshot.Candidates))
            Versions.Add(new HarnessVersionRowViewModel(row));
    }

    // ===== 共通 =====

    private string DisplayPath(string path) => _workspace.Contains(path) ? _workspace.ToDisplayPath(path) : path;

    /// <summary>記録の1行を整形して出す（数 MB の記録をエディタで開かなくても中身が見えるように）。</summary>
    private async Task LoadRecordAsync(string? path, int line, Action<string> apply)
    {
        _detailCts?.Cancel();
        if (path is null || line <= 0)
        {
            apply("");
            return;
        }
        var cts = _detailCts = new CancellationTokenSource();
        string text;
        try
        {
            text = await Task.Run(() => ReadRecord(path, line), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!cts.IsCancellationRequested)
            apply(text);
    }

    private static string ReadRecord(string path, int line)
    {
        try
        {
            var raw = ClaudeTranscriptLocator.ReadLines(path).Skip(line - 1).FirstOrDefault();
            if (raw is null)
                return "（記録の行が見つかりません。記録が書き換わった可能性があります）";
            string pretty;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                pretty = JsonSerializer.Serialize(doc.RootElement, PrettyJson);
            }
            catch (JsonException)
            {
                pretty = raw;
            }
            return pretty.Length <= DetailMaxChars ? pretty : pretty[..DetailMaxChars] + "\n…（以下略）";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"（記録を読めませんでした: {ex.Message}）";
        }
    }

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
