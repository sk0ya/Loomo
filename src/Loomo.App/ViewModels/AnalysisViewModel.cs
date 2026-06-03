using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Diff;
using sk0ya.Loomo.Core.Observability;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>解析対象に出すトレースセッション1件。</summary>
public sealed record TraceSessionItem(string SessionId, string Title, DateTime UpdatedAt, long SizeBytes)
{
    public string Display => $"{Title}  —  {UpdatedAt:yyyy/MM/dd HH:mm}";
}

/// <summary>メトリクス表示の1行（ラベル/値）。</summary>
public sealed record MetricRow(string Label, string Value);

/// <summary>
/// AIログ分析パネルの ViewModel（観測性 Phase B + AI改善提案）。
/// トレースを集計してメトリクスを表示し、AIに改善提案（システムプロンプト改訂案）を生成させて反映する。
/// </summary>
public sealed partial class AnalysisViewModel : ObservableObject
{
    private readonly TraceReader _reader;
    private readonly ImprovementAdvisor _advisor;
    private readonly AiSettings _settings;
    private readonly AiSettingsStore _store;
    private readonly ToolRegistry _tools;
    private readonly ConversationStore _conversations;

    private CancellationTokenSource? _cts;
    private string? _proposedPrompt;
    private string? _backupPrompt;

    public ObservableCollection<TraceSessionItem> Sessions { get; } = new();
    public ObservableCollection<MetricRow> Metrics { get; } = new();
    public ObservableCollection<ToolStat> ToolStats { get; } = new();
    public ObservableCollection<DiffLineVm> PromptDiff { get; } = new();

    [ObservableProperty] private TraceSessionItem? _selectedSession;
    [ObservableProperty] private bool _crossSession;
    [ObservableProperty] private string _reportText = "";
    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private bool _hasToolStats;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasProposedPrompt;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private string _statusText = "";

    public AnalysisViewModel(
        TraceReader reader,
        ImprovementAdvisor advisor,
        AiSettings settings,
        AiSettingsStore store,
        ToolRegistry tools,
        ConversationStore conversations)
    {
        _reader = reader;
        _advisor = advisor;
        _settings = settings;
        _store = store;
        _tools = tools;
        _conversations = conversations;
        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        var titles = _conversations.List().ToDictionary(s => s.Id, s => s.Title);
        var keep = SelectedSession?.SessionId;

        Sessions.Clear();
        foreach (var f in _reader.List())
        {
            var title = titles.TryGetValue(f.SessionId, out var t) ? t : f.SessionId;
            Sessions.Add(new TraceSessionItem(f.SessionId, title, f.UpdatedAt, f.SizeBytes));
        }

        SelectedSession = Sessions.FirstOrDefault(s => s.SessionId == keep) ?? Sessions.FirstOrDefault();
        RecomputeMetrics();
    }

    partial void OnSelectedSessionChanged(TraceSessionItem? value)
    {
        RecomputeMetrics();
        AnalyzeCommand.NotifyCanExecuteChanged();
    }

    partial void OnCrossSessionChanged(bool value)
    {
        RecomputeMetrics();
        AnalyzeCommand.NotifyCanExecuteChanged();
    }

    private void RecomputeMetrics()
    {
        Metrics.Clear();
        ToolStats.Clear();

        if (CrossSession)
        {
            var all = _reader.List()
                .Select(f => SessionMetrics.Compute(f.SessionId, _reader.Read(f.SessionId)))
                .ToList();
            var c = MetricsAggregator.Compute(all);
            StatusText = $"横断: {c.SessionCount} セッション";
            Add("セッション数", c.SessionCount.ToString());
            Add("ターン数", c.TurnCount.ToString());
            Add("平均反復/ターン", $"{c.AvgIterations:0.0}");
            Add("ツール呼出(失敗)", $"{c.ToolCallCount} ({c.ToolErrorCount})");
            Add("安全ブロック", c.SafetyBlockCount.ToString());
            Add("承認回数", c.ApprovalCount.ToString());
            Add("平均承認待ち", $"{c.AvgApprovalWaitMs:0} ms");
            AddTtft(c.AvgTimeToFirstTokenMs);
            AddTokens(c.TotalInputTokens, c.TotalOutputTokens);
            foreach (var t in c.ToolStats) ToolStats.Add(t);
        }
        else if (SelectedSession is { } s)
        {
            var m = SessionMetrics.Compute(s.SessionId, _reader.Read(s.SessionId));
            StatusText = s.Title;
            Add("プロバイダ", m.Provider ?? "—");
            Add("ターン数", m.TurnCount.ToString());
            Add("平均反復/ターン", $"{m.AvgIterations:0.0}");
            Add("ツール呼出(失敗)", $"{m.ToolCallCount} ({m.ToolErrorCount})");
            Add("安全ブロック", m.SafetyBlockCount.ToString());
            Add("承認(許可)", $"{m.ApprovalCount} ({m.ApprovalApprovedCount})");
            Add("平均承認待ち", $"{m.AvgApprovalWaitMs:0} ms");
            AddTtft(m.AvgTimeToFirstTokenMs);
            Add("ターン総所要", $"{m.TotalTurnDurationMs:0} ms");
            Add("エラー件数", m.Errors.Count.ToString());
            AddTokens(m.TotalInputTokens, m.TotalOutputTokens);
            foreach (var t in m.ToolStats) ToolStats.Add(t);
        }
        else
        {
            StatusText = "トレースがありません";
        }

        HasToolStats = ToolStats.Count > 0;

        void Add(string label, string value) => Metrics.Add(new MetricRow(label, value));
        void AddTtft(double? ttft) { if (ttft is { } v) Add("平均TTFT", $"{v:0} ms"); }
        void AddTokens(long? inTok, long? outTok)
        {
            if (inTok is { } i && outTok is { } o) Add("トークン(入/出)", $"{i} / {o}");
        }
    }

    private bool CanAnalyze() => !IsBusy && (CrossSession || SelectedSession is not null);

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        IsBusy = true;
        ReportText = "";
        ClearProposal();
        _cts = new CancellationTokenSource();
        try
        {
            var input = BuildAdvisorInput();
            await foreach (var chunk in _advisor.AnalyzeAsync(input, _cts.Token))
                ReportText += chunk;

            BuildPromptDiff();
        }
        catch (OperationCanceledException)
        {
            ReportText += "\n\n⏹ 中断されました。";
        }
        catch (Exception ex)
        {
            ReportText += $"\n\n⚠️ 例外: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private AdvisorInput BuildAdvisorInput()
    {
        var toolInfos = _tools.Definitions.Select(d => new ToolInfo(d.Name, d.Description)).ToList();

        if (CrossSession)
        {
            // 各トレースファイルの読取は1回だけ（メトリクスと失敗サンプルで共有）。
            var files = _reader.List();
            var perSession = new List<SessionMetrics>(files.Count);
            var samples = new List<string>();
            foreach (var f in files)
            {
                var events = _reader.Read(f.SessionId);
                perSession.Add(SessionMetrics.Compute(f.SessionId, events));
                if (samples.Count < 20)
                    samples.AddRange(ImprovementAdvisor.BuildFailureSamples(events, max: 4));
            }
            if (samples.Count > 20) samples = samples.Take(20).ToList();
            var cross = MetricsAggregator.Compute(perSession);
            return new AdvisorInput(_settings.SystemPrompt, toolInfos, null, cross, samples);
        }
        else
        {
            var events = _reader.Read(SelectedSession!.SessionId);
            var single = SessionMetrics.Compute(SelectedSession.SessionId, events);
            var samples = ImprovementAdvisor.BuildFailureSamples(events);
            return new AdvisorInput(_settings.SystemPrompt, toolInfos, single, null, samples);
        }
    }

    private void BuildPromptDiff()
    {
        var revised = ImprovementAdvisor.ExtractRevisedPrompt(ReportText);
        if (string.IsNullOrWhiteSpace(revised) || revised.Trim() == _settings.SystemPrompt.Trim())
            return;

        _proposedPrompt = revised;
        var unified = DiffUtil.ToUnifiedText(DiffUtil.Compute(_settings.SystemPrompt, revised));
        BuildDiffLines(unified);
        HasProposedPrompt = true;
    }

    private void BuildDiffLines(string unified)
    {
        PromptDiff.Clear();
        foreach (var raw in unified.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length == 0) { PromptDiff.Add(new DiffLineVm(DiffLineKind.Context, "")); continue; }
            var (kind, text) = raw[0] switch
            {
                '+' => (DiffLineKind.Added, raw[1..]),
                '-' => (DiffLineKind.Removed, raw[1..]),
                '⋯' => (DiffLineKind.Gap, raw[1..]),
                ' ' => (DiffLineKind.Context, raw[1..]),
                _ => (DiffLineKind.Context, raw)
            };
            PromptDiff.Add(new DiffLineVm(kind, text));
        }
    }

    private bool CanApply() => HasProposedPrompt && !string.IsNullOrEmpty(_proposedPrompt);

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void ApplyPrompt()
    {
        if (_proposedPrompt is null) return;
        _backupPrompt = _settings.SystemPrompt;
        _settings.SystemPrompt = _proposedPrompt;
        if (TrySave())
        {
            CanUndo = true;
            StatusText = "システムプロンプトを反映しました";
        }
    }

    private bool CanUndoApply() => CanUndo && _backupPrompt is not null;

    [RelayCommand(CanExecute = nameof(CanUndoApply))]
    private void UndoPrompt()
    {
        if (_backupPrompt is null) return;
        _settings.SystemPrompt = _backupPrompt;
        if (TrySave())
        {
            CanUndo = false;
            StatusText = "システムプロンプトを元に戻しました";
        }
    }

    private bool TrySave()
    {
        try { _store.Save(_settings); return true; }
        catch (Exception ex)
        {
            MessageBox.Show($"設定の保存に失敗しました: {ex.Message}", "Loomo",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void ClearProposal()
    {
        HasProposedPrompt = false;
        _proposedPrompt = null;
        PromptDiff.Clear();
    }

    partial void OnReportTextChanged(string value) => HasReport = !string.IsNullOrEmpty(value);
    partial void OnIsBusyChanged(bool value) => AnalyzeCommand.NotifyCanExecuteChanged();
    partial void OnHasProposedPromptChanged(bool value) => ApplyPromptCommand.NotifyCanExecuteChanged();
    partial void OnCanUndoChanged(bool value) => UndoPromptCommand.NotifyCanExecuteChanged();
}
