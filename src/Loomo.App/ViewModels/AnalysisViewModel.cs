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
using sk0ya.Loomo.Core.Observability;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>解析対象に出すトレースセッション1件。</summary>
public sealed record TraceSessionItem(string SessionId, string Title, DateTime UpdatedAt, long SizeBytes)
{
    public string Display => $"{Title}  —  {UpdatedAt:yyyy/MM/dd HH:mm}";
}

/// <summary>複数選択リストの1行（チェック状態付き）。</summary>
public sealed partial class SelectableSession : ObservableObject
{
    private readonly Action _onChanged;
    public TraceSessionItem Item { get; }
    public string SessionId => Item.SessionId;
    public string Display => Item.Display;

    [ObservableProperty] private bool _isSelected;

    public SelectableSession(TraceSessionItem item, bool selected, Action onChanged)
    {
        Item = item;
        _isSelected = selected; // フィールド直接設定で初期化中の通知を避ける
        _onChanged = onChanged;
    }

    partial void OnIsSelectedChanged(bool value) => _onChanged();
}

/// <summary>メトリクス表示の1行（ラベル/値）。</summary>
public sealed record MetricRow(string Label, string Value);

/// <summary>
/// AIログ分析パネルの ViewModel（観測性 Phase B + AI改善提案）。
/// トレースを集計してメトリクスを表示し、AIに改善提案を生成させる。
/// </summary>
public sealed partial class AnalysisViewModel : ObservableObject
{
    private readonly TraceReader _reader;
    private readonly ImprovementAdvisor _advisor;
    private readonly AiSettings _settings;
    private readonly ToolRegistry _tools;
    private readonly ConversationStore _conversations;

    private CancellationTokenSource? _cts;
    private bool _suspendRecompute;   // 一括選択変更中の再集計を抑止

    // 選択変更のたびに同じトレースを読み直さないためのキャッシュ（Refresh で破棄）。
    private readonly Dictionary<string, IReadOnlyList<TraceEvent>> _eventCache = new();

    public ObservableCollection<SelectableSession> Sessions { get; } = new();
    public ObservableCollection<MetricRow> Metrics { get; } = new();
    public ObservableCollection<ToolStat> ToolStats { get; } = new();

    [ObservableProperty] private string _reportText = "";
    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private bool _hasToolStats;
    [ObservableProperty] private bool _isBusy;
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
        _tools = tools;
        _conversations = conversations;
        // セッション保存（ターン終了）ごとに一覧を更新し、AIセッション一覧と歩調を合わせる。
        _conversations.Changed += OnConversationsChanged;
        Refresh();
    }

    private void OnConversationsChanged()
    {
        var app = Application.Current;
        if (app is null || app.Dispatcher.CheckAccess()) Refresh();
        else app.Dispatcher.Invoke(Refresh);
    }

    [RelayCommand]
    public void Refresh()
    {
        var titles = _conversations.List().ToDictionary(s => s.Id, s => s.Title);
        var prevSelected = Sessions.Where(s => s.IsSelected).Select(s => s.SessionId).ToHashSet();
        var hadAny = Sessions.Count > 0;

        // ファイルが更新されている可能性があるので読取キャッシュは破棄する。
        _eventCache.Clear();

        _suspendRecompute = true;
        Sessions.Clear();
        foreach (var f in _reader.List())
        {
            var title = titles.TryGetValue(f.SessionId, out var t) ? t : f.SessionId;
            var item = new TraceSessionItem(f.SessionId, title, f.UpdatedAt, f.SizeBytes);
            Sessions.Add(new SelectableSession(item, prevSelected.Contains(f.SessionId), OnSelectionChanged));
        }

        // 初回表示は先頭1件を既定選択にしておく。
        if (!hadAny && Sessions.Count > 0 && !Sessions.Any(s => s.IsSelected))
            Sessions[0].IsSelected = true;
        _suspendRecompute = false;

        RecomputeMetrics();
        AnalyzeCommand.NotifyCanExecuteChanged();
    }

    private void OnSelectionChanged()
    {
        if (_suspendRecompute) return;
        RecomputeMetrics();
        AnalyzeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectAll() => SetAllSelected(true);

    [RelayCommand]
    private void ClearSelection() => SetAllSelected(false);

    private void SetAllSelected(bool selected)
    {
        _suspendRecompute = true;
        foreach (var s in Sessions) s.IsSelected = selected;
        _suspendRecompute = false;
        OnSelectionChanged();
    }

    private List<SelectableSession> SelectedSessions() => Sessions.Where(s => s.IsSelected).ToList();

    /// <summary>トレースを読む（同一セッションは Refresh まで使い回す）。選択変更ごとの再読込を避ける。</summary>
    private IReadOnlyList<TraceEvent> ReadEvents(string sessionId)
    {
        if (!_eventCache.TryGetValue(sessionId, out var events))
            _eventCache[sessionId] = events = _reader.Read(sessionId);
        return events;
    }

    private void RecomputeMetrics()
    {
        Metrics.Clear();
        ToolStats.Clear();

        var selected = SelectedSessions();

        if (selected.Count == 0)
        {
            StatusText = Sessions.Count == 0 ? "トレースがありません" : "セッションを選択してください";
            HasToolStats = false;
            return;
        }

        if (selected.Count > 1)
        {
            var all = selected
                .Select(s => SessionMetrics.Compute(s.SessionId, ReadEvents(s.SessionId)))
                .ToList();
            var c = MetricsAggregator.Compute(all);
            StatusText = $"選択 {c.SessionCount} 件を集計";
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
        else
        {
            var s = selected[0];
            var m = SessionMetrics.Compute(s.SessionId, ReadEvents(s.SessionId));
            StatusText = s.Item.Title;
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

        HasToolStats = ToolStats.Count > 0;

        void Add(string label, string value) => Metrics.Add(new MetricRow(label, value));
        void AddTtft(double? ttft) { if (ttft is { } v) Add("平均TTFT", $"{v:0} ms"); }
        void AddTokens(long? inTok, long? outTok)
        {
            if (inTok is { } i && outTok is { } o) Add("トークン(入/出)", $"{i} / {o}");
        }
    }

    private bool CanAnalyze() => !IsBusy && Sessions.Any(s => s.IsSelected);

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        IsBusy = true;
        ReportText = "";
        _cts = new CancellationTokenSource();
        try
        {
            var input = BuildAdvisorInput();
            await foreach (var chunk in _advisor.AnalyzeAsync(input, _cts.Token))
                ReportText += chunk;
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
        var selected = SelectedSessions();

        if (selected.Count > 1)
        {
            // 選択した各トレースファイルの読取は1回だけ（メトリクスと失敗サンプルで共有）。
            var perSession = new List<SessionMetrics>(selected.Count);
            var samples = new List<string>();
            foreach (var s in selected)
            {
                var events = ReadEvents(s.SessionId);
                perSession.Add(SessionMetrics.Compute(s.SessionId, events));
                if (samples.Count < 20)
                    samples.AddRange(ImprovementAdvisor.BuildFailureSamples(events, max: 4));
            }
            if (samples.Count > 20) samples = samples.Take(20).ToList();
            var cross = MetricsAggregator.Compute(perSession);
            return new AdvisorInput(_settings.SystemPrompt, toolInfos, null, cross, samples);
        }
        else
        {
            var id = selected[0].SessionId;
            var events = ReadEvents(id);
            var single = SessionMetrics.Compute(id, events);
            var samples = ImprovementAdvisor.BuildFailureSamples(events);
            return new AdvisorInput(_settings.SystemPrompt, toolInfos, single, null, samples);
        }
    }

    partial void OnReportTextChanged(string value) => HasReport = !string.IsNullOrEmpty(value);
    partial void OnIsBusyChanged(bool value) => AnalyzeCommand.NotifyCanExecuteChanged();
}
