using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// ワークフローモードの ViewModel。ユーザーが手で並べた「単発のAI指示」（ステップ）を順に実行し、
/// 前段の出力を <c>{{1}}</c> 等のプレースホルダで後段へ渡す。チャットとワークフローで
/// ウォームアップ済みプレフィックスを共有できるよう、モデルへ提示するツール定義は同一に保つ。
/// </summary>
/// <summary>「ステップを追加」パレットのカテゴリ見出し＋その候補群。</summary>
public sealed record StepCandidateGroup(string Category, IReadOnlyList<WorkflowStepCandidate> Items);

public sealed partial class WorkflowViewModel : ObservableObject
{
    private readonly AgentOrchestrator _orchestrator;
    private readonly UiApprovalService _approval;
    private readonly WorkflowStore _store;
    private readonly IAiWarmup _warmup;
    private readonly AiSettings _settings;

    private CancellationTokenSource? _cts;
    private WorkflowStepViewModel? _runningStep;   // 承認カードの差し込み先
    private string? _currentId;                     // 読込済みワークフローのID（保存時に上書き）
    private bool _suppressChangeTracking;
    private readonly DispatcherTimer _warmupTimer;  // ウォームアップ経過秒の表示を更新する

    // チャットと同じ「動作中だと分かる」経過秒つきステータス（実行中ステップの StatusText を刻む）。
    private readonly DispatcherTimer _statusTimer;   // 経過秒の表示を更新する
    private readonly Stopwatch _statusClock = new(); // 現フェーズ開始からの経過時間
    private string _statusPhase = "";                // 経過秒を除いたフェーズ説明

    public ObservableCollection<WorkflowStepViewModel> Steps { get; } = new();

    /// <summary>「ステップを追加」パレットに出す、カテゴリ別のステップ候補ライブラリ（組み込み・不変）。</summary>
    public IReadOnlyList<StepCandidateGroup> StepLibrary { get; } = WorkflowStepLibrary.Catalog
        .GroupBy(c => c.Category)
        .Select(g => new StepCandidateGroup(g.Key, g.ToList()))
        .ToList();

    /// <summary>「ステップを追加」パレット（2ペイン）で左ペインの選択中カテゴリ。右ペインの候補一覧を決める。</summary>
    [ObservableProperty] private StepCandidateGroup? _selectedStepCategory;

    /// <summary>右ペインに出す、選択中カテゴリの候補。未選択なら空。</summary>
    public IReadOnlyList<WorkflowStepCandidate> VisibleCandidates =>
        SelectedStepCategory?.Items ?? System.Array.Empty<WorkflowStepCandidate>();

    partial void OnSelectedStepCategoryChanged(StepCandidateGroup? value) =>
        OnPropertyChanged(nameof(VisibleCandidates));

    /// <summary>左ペインでカテゴリを選ぶ。右ペインを切り替えるだけ（ステップは追加しない）。</summary>
    [RelayCommand]
    private void SelectStepCategory(StepCandidateGroup? group)
    {
        if (group is not null) SelectedStepCategory = group;
    }

    /// <summary>読込ドロップダウンに出す保存済みワークフロー一覧。</summary>
    public ObservableCollection<WorkflowSummary> SavedWorkflows { get; } = new();

    [ObservableProperty] private string _name = "新しいワークフロー";
    [ObservableProperty] private bool _isRunning;

    /// <summary>実行中の全体状況（「ステップ2/3 を実行中…」等）。</summary>
    [ObservableProperty] private string _runStatus = "";
    [ObservableProperty] private bool _hasUnsavedChanges;

    public string SaveStatus => HasUnsavedChanges
        ? "自動保存中..."
        : _currentId is null ? "未保存" : "自動保存済み";

    public string? CurrentWorkflowId => _currentId;

    public string WorkflowSummaryText
    {
        get
        {
            var runnable = Steps.Count(s => !string.IsNullOrWhiteSpace(s.Prompt));
            if (Steps.Count == 0) return "ステップなし";
            var skipped = Steps.Count - runnable;
            return skipped == 0
                ? $"{runnable} ステップを実行"
                : $"{runnable} ステップを実行、{skipped} 件スキップ";
        }
    }

    public WorkflowViewModel(
        AgentOrchestrator orchestrator,
        UiApprovalService approval,
        WorkflowStore store,
        IAiWarmup warmup,
        AiSettings settings)
    {
        _orchestrator = orchestrator;
        _approval = approval;
        _store = store;
        _warmup = warmup;
        _settings = settings;

        _approval.ApprovalRequested += OnApprovalRequested;
        _store.Changed += () => Dispatch(RefreshSavedWorkflows);

        _warmupTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _warmupTimer.Tick += (_, _) => RenderWarmupStatus();
        _warmup.StateChanged += OnWarmupStateChanged;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) => RenderStepStatus();

        // 「ステップを追加」パレットは先頭カテゴリを開いた状態で出す。
        _selectedStepCategory = StepLibrary.Count > 0 ? StepLibrary[0] : null;

        // ステップはユーザーが明示的に追加するまで作らない。
    }

    /// <summary>ウォームアップ状態の変化を、実行中ステータスバーへ反映する。ワークフロー実行中の
    /// 暖機フェーズだけ表示し、待機中は何もしない（チャット側 AI バーと役割を分ける）。</summary>
    private void OnWarmupStateChanged() => Dispatch(() =>
    {
        if (!IsRunning) return;
        if (_warmup.IsWarmingUp)
        {
            if (!_warmupTimer.IsEnabled) _warmupTimer.Start();
            RenderWarmupStatus();
        }
        else
        {
            _warmupTimer.Stop();
        }
    });

    private void RenderWarmupStatus()
    {
        if (!IsRunning || !_warmup.IsWarmingUp || _warmup.WarmupStartedAt is not { } startedAt) return;
        var elapsed = DateTimeOffset.Now - startedAt;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var current = string.IsNullOrWhiteSpace(_warmup.CurrentStatus)
            ? "モデルとプロンプトを準備しています"
            : _warmup.CurrentStatus;
        RunStatus = $"ウォームアップ中… {current}。{FormatWarmupDuration(elapsed)} 経過";
    }

    private static string FormatWarmupDuration(TimeSpan elapsed) =>
        elapsed.TotalMinutes < 1
            ? $"{Math.Floor(elapsed.TotalSeconds):0} 秒"
            : $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒";

    // ===== 実行中ステップの経過秒つきステータス（チャットの SetStatus と同等） =====

    /// <summary>実行中ステップのフェーズを切り替え、経過秒の表示更新を開始する。</summary>
    private void SetStepStatus(string phase)
    {
        _statusPhase = phase;
        if (!_statusClock.IsRunning) _statusClock.Restart();
        if (!_statusTimer.IsEnabled) _statusTimer.Start();
        RenderStepStatus();
    }

    /// <summary>フェーズ文言に経過秒を付けて、実行中ステップの StatusText を更新する。</summary>
    private void RenderStepStatus()
    {
        if (_runningStep is null || string.IsNullOrEmpty(_statusPhase)) return;
        _runningStep.StatusText = $"{_statusPhase} （{_statusClock.Elapsed.TotalSeconds:0}秒）";
    }

    /// <summary>ステップ終了：タイマー・経過時計を止める（最終文言は呼び出し側が上書きする）。</summary>
    private void ClearStepStatus()
    {
        _statusTimer.Stop();
        _statusClock.Reset();
        _statusPhase = "";
    }

    private static void Dispatch(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app is null || app.Dispatcher.CheckAccess()) action();
        else app.Dispatcher.BeginInvoke(action);
    }

    /// <summary>承認要求を「いま実行中のステップ」のログへ橋渡しする。ワークフロー実行中以外は
    /// 何もしない（チャット側 <see cref="AiBarViewModel.OnApprovalRequested"/> と排他にして二重処理を防ぐ）。</summary>
    private void OnApprovalRequested(ApprovalContext ctx)
    {
        if (!IsRunning || _runningStep is null) return;
        var entry = new TranscriptEntry
        {
            Kind = EntryKind.Approval,
            Header = $"承認が必要: {ctx.ToolName}",
            Text = ctx.Summary,
        };
        if (TranscriptFormatting.ContainsDiff(ctx.Summary))
            entry.SetDiff(ctx.Summary);
        entry.BindApproval(ctx.Completion);
        _runningStep.Log.Add(entry);
    }

    // ===== ステップ編集 =====

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddStep()
    {
        var step = new WorkflowStepViewModel();
        AttachStepChangeTracking(step);
        Steps.Add(step);
        Renumber();
        MarkDirty();
    }

    /// <summary>ライブラリのステップ候補（null なら空ステップ）を末尾に追加する。「ステップを追加」パレットから呼ばれる。</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddCandidate(WorkflowStepCandidate? candidate)
    {
        var step = candidate is null ? new WorkflowStepViewModel() : new WorkflowStepViewModel(candidate.ToStep());
        AttachStepChangeTracking(step);
        Steps.Add(step);
        Renumber();
        MarkDirty();
    }

    private bool CanEdit() => !IsRunning;

    /// <summary>指定ステップの直後（null なら末尾）に空ステップを差し込む。パイプラインの「＋」から呼ばれる。</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void InsertStepAfter(WorkflowStepViewModel? step)
    {
        var i = step is null ? Steps.Count - 1 : Steps.IndexOf(step);
        var inserted = new WorkflowStepViewModel();
        AttachStepChangeTracking(inserted);
        Steps.Insert(i + 1, inserted);
        Renumber();
        MarkDirty();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RemoveStep(WorkflowStepViewModel? step)
    {
        if (step is null) return;
        Steps.Remove(step);
        Renumber();
        MarkDirty();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void MoveUp(WorkflowStepViewModel? step)
    {
        if (step is null) return;
        var i = Steps.IndexOf(step);
        if (i > 0) { Steps.Move(i, i - 1); Renumber(); MarkDirty(); }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void MoveDown(WorkflowStepViewModel? step)
    {
        if (step is null) return;
        var i = Steps.IndexOf(step);
        if (i >= 0 && i < Steps.Count - 1) { Steps.Move(i, i + 1); Renumber(); MarkDirty(); }
    }

    /// <summary>並び順（Index）・参照トークン・パイプラインの先頭/末尾フラグを振り直す。</summary>
    private void Renumber()
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            Steps[i].Index = i + 1;
            Steps[i].IsFirst = i == 0;
            Steps[i].IsLast = i == Steps.Count - 1;
        }
        OnPropertyChanged(nameof(WorkflowSummaryText));
    }

    private void AttachStepChangeTracking(WorkflowStepViewModel step)
    {
        step.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(WorkflowStepViewModel.Title) or nameof(WorkflowStepViewModel.Prompt))
                MarkDirty();
        };
    }

    private void MarkDirty()
    {
        if (_suppressChangeTracking) return;

        OnPropertyChanged(nameof(WorkflowSummaryText));
        if (!HasPersistableContent())
        {
            HasUnsavedChanges = false;
            OnPropertyChanged(nameof(SaveStatus));
            return;
        }

        if (!HasUnsavedChanges) HasUnsavedChanges = true;
        OnPropertyChanged(nameof(SaveStatus));
        AutoSave();
    }

    partial void OnNameChanged(string value) => MarkDirty();

    partial void OnHasUnsavedChangesChanged(bool value)
    {
        OnPropertyChanged(nameof(SaveStatus));
    }

    // ===== 永続化 =====

    public void RefreshSavedWorkflows()
    {
        SavedWorkflows.Clear();
        foreach (var s in _store.List()) SavedWorkflows.Add(s);
    }

    private bool HasPersistableContent()
    {
        if (_currentId is not null) return true;
        if (!string.IsNullOrWhiteSpace(Name) && Name.Trim() != "新しいワークフロー") return true;
        return Steps.Any(s => !string.IsNullOrWhiteSpace(s.Title) || !string.IsNullOrWhiteSpace(s.Prompt));
    }

    private void AutoSave()
    {
        var wf = new Workflow
        {
            Id = _currentId,
            Name = string.IsNullOrWhiteSpace(Name) ? "(無題)" : Name.Trim(),
            Steps = Steps.Select(s => s.ToModel()).ToList(),
        };
        _currentId = _store.Save(wf);
        OnPropertyChanged(nameof(CurrentWorkflowId));
        HasUnsavedChanges = false;
        OnPropertyChanged(nameof(SaveStatus));
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void NewWorkflow()
    {
        _suppressChangeTracking = true;
        try
        {
            _currentId = null;
            OnPropertyChanged(nameof(CurrentWorkflowId));
            Name = "新しいワークフロー";
            Steps.Clear();
            HasUnsavedChanges = false;
            RunStatus = "";
        }
        finally
        {
            _suppressChangeTracking = false;
        }
        OnPropertyChanged(nameof(SaveStatus));
        OnPropertyChanged(nameof(WorkflowSummaryText));
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void LoadWorkflow(WorkflowSummary? summary)
    {
        if (summary is null) return;
        var wf = _store.Load(summary.Id);
        if (wf is null) return;

        _suppressChangeTracking = true;
        try
        {
            _currentId = wf.Id;
            OnPropertyChanged(nameof(CurrentWorkflowId));
            Name = wf.Name;
            Steps.Clear();
            foreach (var s in wf.Steps)
            {
                var step = new WorkflowStepViewModel(s);
                AttachStepChangeTracking(step);
                Steps.Add(step);
            }
            Renumber();
            HasUnsavedChanges = false;
            RunStatus = "";
        }
        finally
        {
            _suppressChangeTracking = false;
        }
        OnPropertyChanged(nameof(SaveStatus));
        OnPropertyChanged(nameof(WorkflowSummaryText));
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void DeleteWorkflow(WorkflowSummary? summary)
    {
        if (summary is null) return;
        _store.Delete(summary.Id);
        if (_currentId == summary.Id)
        {
            _currentId = null;
            OnPropertyChanged(nameof(CurrentWorkflowId));
            OnPropertyChanged(nameof(SaveStatus));
        }
    }

    // ===== 実行 =====

    private bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        // 実行できる（指示文が空でない）ステップが無ければ何もしない。
        if (!Steps.Any(s => !string.IsNullOrWhiteSpace(s.Prompt)))
        {
            RunStatus = "実行できるステップがありません（指示文を入力してください）。";
            return;
        }

        IsRunning = true;
        NotifyCommandStates();
        _cts = new CancellationTokenSource();
        var sessionId = Guid.NewGuid().ToString("N");
        var outputs = new List<string>();

        // 実行対象は「指示文が空でない」ステップのみ。空ステップは番号の連続性のため出力に空文字を積む。
        try
        {
            // モデル未ロードなら暖機が走る。完了までステータスバーに進捗（経過秒）を出す。
            if (!_warmup.IsReady)
            {
                _warmupTimer.Start();
                RenderWarmupStatus();
                if (string.IsNullOrEmpty(RunStatus)) RunStatus = "ウォームアップ中…";
            }
            await _warmup.EnsureWarmAsync(_cts.Token);
            _warmupTimer.Stop();

            for (var i = 0; i < Steps.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var step = Steps[i];
                if (string.IsNullOrWhiteSpace(step.Prompt))
                {
                    step.ResetRun();
                    step.StatusText = "（空のためスキップ）";
                    outputs.Add("");
                    continue;
                }

                RunStatus = $"ステップ {i + 1}/{Steps.Count} を実行中…";
                var (output, ok) = await RunStepAsync(step, outputs, sessionId, _cts.Token);
                outputs.Add(output);
                if (!ok)
                {
                    RunStatus = $"ステップ {i + 1} で停止しました。";
                    return;   // 連鎖を打ち切る
                }
            }

            RunStatus = "完了しました。";
        }
        catch (OperationCanceledException)
        {
            RunStatus = "中断しました。";
            if (_runningStep is not null)
            {
                _runningStep.Status = WorkflowStepStatus.Error;
                _runningStep.StatusText = "中断";
            }
        }
        catch (Exception ex)
        {
            RunStatus = "エラーで停止しました。";
            _runningStep?.Log.Add(new TranscriptEntry
            {
                Kind = EntryKind.Error, Header = "⚠️ 例外", Text = ex.Message,
            });
        }
        finally
        {
            _warmupTimer.Stop();
            ClearStepStatus();
            _runningStep = null;
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
            NotifyCommandStates();
        }
    }

    /// <summary>1ステップを実行し、(最終出力, 成功か) を返す。</summary>
    private async Task<(string Output, bool Ok)> RunStepAsync(
        WorkflowStepViewModel step, IReadOnlyList<string> outputs, string sessionId, CancellationToken ct)
    {
        step.ResetRun();
        step.Status = WorkflowStepStatus.Running;
        step.IsLogCollapsed = false;   // 実行中は進捗を見せる
        _runningStep = step;

        var prompt = WorkflowPrompt.Resolve(step.Prompt, outputs);
        var conversation = new Conversation();
        var clock = Stopwatch.StartNew();

        // チャットと同じく、ステップ先頭に「進行状況」エントリ（タイムスタンプ付きの逐次ログ）を置く。
        var activity = AddLog(step, EntryKind.Activity, "進行状況", "");
        var rawStream = new StringBuilder();   // 現在のAI呼び出しの揮発性ライブ出力（進捗プレビュー専用）
        var volatileTail = "";                 // 「進行状況」末尾に付けている揮発プレビュー文字列（未保存）
        var aiCallCount = 0;
        var loggedThinking = false;
        var loggedResponse = false;

        TranscriptEntry? assistant = null;
        TranscriptEntry? thinking = null;
        var finalText = "";
        var ok = true;

        // 「進行状況」エントリ末尾に揮発プレビューを挟まないよう、恒久ログを足す前に末尾を片付ける。
        void AppendActivity(string message)
        {
            ClearVolatile();
            var prefix = activity.Text.Length == 0 ? "" : Environment.NewLine;
            activity.AppendText($"{prefix}[{TranscriptFormatting.FormatDuration(clock.Elapsed)}] {message}");
        }

        void SetVolatile(string preview)
        {
            ClearVolatile();
            if (preview.Length == 0) return;
            var prefix = activity.Text.Length == 0 ? "" : Environment.NewLine;
            volatileTail = prefix + preview;
            activity.AppendText(volatileTail);
        }

        void ClearVolatile()
        {
            if (volatileTail.Length == 0) return;
            activity.Text = activity.Text[..^volatileTail.Length];
            volatileTail = "";
        }

        SetStepStatus("実行中…");
        AppendActivity(TranscriptFormatting.FormatRunConfig(_settings));
        AppendActivity("AIに送信しました。応答を待っています。");

        try
        {
            await foreach (var ev in _orchestrator.RunTurnAsync(
                               conversation, prompt, sessionId, ct, turnPreamble: AiSettings.WorkflowTurnPreamble))
            {
                switch (ev)
                {
                    case ThinkingDelta t:
                        if (!loggedThinking) { AppendActivity("モデルが思考を生成しています。"); loggedThinking = true; }
                        thinking ??= AddLog(step, EntryKind.Thinking, "💭 思考", "");
                        thinking.AppendText(t.Text);
                        SetStepStatus($"💭 思考中… {TranscriptFormatting.StreamPreview(thinking.Text)}");
                        break;

                    case RawTextDelta raw:
                        // 揮発性のライブ出力：ログには残さず「進行状況」末尾に生成中の生テキストを逐次プレビューする。
                        if (!loggedResponse) { AppendActivity("回答本文の生成を開始しました。"); SetStepStatus("応答生成中…"); loggedResponse = true; }
                        rawStream.Append(raw.Text);
                        var preview = rawStream.ToString().Trim();
                        SetVolatile(preview.Length == 0 ? "" : $"💬 生成中:{Environment.NewLine}{preview}");
                        break;

                    case TextDelta d:
                        if (!loggedResponse) { AppendActivity("回答本文の生成を開始しました。"); loggedResponse = true; }
                        thinking = null;
                        assistant ??= AddLog(step, EntryKind.Assistant, "エージェント", "");
                        assistant.AppendText(d.Text);
                        SetStepStatus("応答生成中…");
                        break;

                    case ToolUseRequested req:
                        thinking = null;
                        rawStream.Clear();
                        SetVolatile("");   // ツール確定で配列の生JSONを見せていた揮発プレビューは消す
                        var narration = assistant?.Text;
                        if (assistant is not null) { step.Log.Remove(assistant); assistant = null; }
                        SetStepStatus($"🔧 {req.ToolUse.Name} を準備中… {TranscriptFormatting.StreamPreview(req.ToolUse.ArgumentsJson)}");
                        AppendActivity($"{req.ToolUse.Name} の呼び出しを準備しています: {TranscriptFormatting.StreamPreview(req.ToolUse.ArgumentsJson)}");
                        AddLog(step, EntryKind.Tool,
                            TranscriptFormatting.ToolUseHeader(req.ToolUse.Name, req.ToolUse.ArgumentsJson),
                            TranscriptFormatting.ComposeToolCard(narration, req.ToolUse.ArgumentsJson, req.ToolUse.RawJson),
                            collapsed: true);
                        break;

                    case ApprovalRequested ap:
                        SetStepStatus($"⏳ {ap.ToolName} の承認待ち…");
                        AppendActivity($"{ap.ToolName} の実行承認を待っています。");
                        break;

                    case ToolExecutionStarted started:
                        SetStepStatus($"🔧 {started.ToolUse.Name} を実行中… {TranscriptFormatting.StreamPreview(started.ToolUse.ArgumentsJson)}");
                        AppendActivity($"{started.ToolUse.Name} を実行しています: {TranscriptFormatting.StreamPreview(started.ToolUse.ArgumentsJson)}");
                        break;

                    case ToolExecutionCompleted done:
                        SetStepStatus($"考え中…（直前 {done.ToolUse.Name}: {(done.Result.IsError ? "エラー" : "完了")}）");
                        AppendActivity($"{done.ToolUse.Name} が完了しました（{(done.Result.IsError ? "エラー" : "成功")}）: {TranscriptFormatting.StreamPreview(done.Result.Content)}。結果を踏まえて次の応答を待っています。");
                        AddLog(step, EntryKind.Tool, $"↳ 結果 ({done.ToolUse.Name})",
                            TranscriptFormatting.Truncate(done.Result.Content), collapsed: true);
                        break;

                    case ToolCallParseFailed pf:
                        if (assistant is not null) { step.Log.Remove(assistant); assistant = null; }
                        SetStepStatus("⚠️ ツール呼び出しJSONが不正。AIに再試行させています…");
                        AppendActivity("ツール呼び出しのJSONが不正でした。モデルの生出力を表示し、正しいJSONで出し直させます。");
                        AddLog(step, EntryKind.Error, "⚠️ 不正なツール出力（再試行）", pf.RawText);
                        break;

                    case AgentError err:
                        AppendActivity("エラーで停止しました。");
                        AddLog(step, EntryKind.Error, "⚠️ エラー", err.Message);
                        ok = false;
                        break;

                    case AiUsageReported usage:
                        aiCallCount++;
                        AppendActivity(TranscriptFormatting.FormatUsage(usage, aiCallCount));
                        rawStream.Clear();   // このAI呼び出しは終了。次の呼び出しの揮発プレビューを新規に始める
                        break;

                    case TurnCompleted tc:
                        finalText = tc.FinalText ?? assistant?.Text ?? "";
                        break;
                }
            }
        }
        finally
        {
            ClearVolatile();   // 揮発プレビューが残ったままにならないよう必ず片付ける
            ClearStepStatus();
        }

        clock.Stop();
        activity.Header = $"進行状況 ({TranscriptFormatting.FormatDuration(clock.Elapsed)})";
        if (ok)
        {
            step.Status = WorkflowStepStatus.Done;
            step.StatusText = $"完了 ({TranscriptFormatting.FormatDuration(clock.Elapsed)})";
            step.Output = finalText;
            // 出力エントリ（後続へ渡る素のテキスト）を見やすく1件残す。
            if (!string.IsNullOrWhiteSpace(finalText))
                AddLog(step, EntryKind.Assistant, "出力", finalText);
        }
        else
        {
            step.Status = WorkflowStepStatus.Error;
            step.StatusText = $"失敗 ({TranscriptFormatting.FormatDuration(clock.Elapsed)})";
        }

        _runningStep = null;
        return (finalText, ok);
    }

    private TranscriptEntry AddLog(WorkflowStepViewModel step, EntryKind kind, string header, string text, bool collapsed = false)
    {
        var entry = new TranscriptEntry { Kind = kind, Header = header, Text = text, IsCollapsed = collapsed };
        step.Log.Add(entry);
        return entry;
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private void NotifyCommandStates()
    {
        RunCommand.NotifyCanExecuteChanged();
        AddStepCommand.NotifyCanExecuteChanged();
        AddCandidateCommand.NotifyCanExecuteChanged();
        InsertStepAfterCommand.NotifyCanExecuteChanged();
        RemoveStepCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        NewWorkflowCommand.NotifyCanExecuteChanged();
        LoadWorkflowCommand.NotifyCanExecuteChanged();
        DeleteWorkflowCommand.NotifyCanExecuteChanged();
    }
}
