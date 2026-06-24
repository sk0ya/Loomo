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
    private readonly WorkflowToolRunner _toolRunner;

    private CancellationTokenSource? _cts;
    private WorkflowStepViewModel? _runningStep;   // 経過秒つきステータスの差し込み先（実行中ステップ）
    private string? _currentId;                     // 読込済みワークフローのID（保存時に上書き）
    private bool _suppressChangeTracking;
    private readonly DispatcherTimer _warmupTimer;  // ウォームアップ経過秒の表示を更新する

    // 実行ログはステップ単位でまとめず、全ステップを1本のフラットな進捗タイムラインに流す
    // （チャットの「進行状況」と同じ ActivityTimelineView で見せる）。
    private readonly Stopwatch _runClock = new();   // 実行全体の経過時間（各段の時刻に使う）
    private ActivityLog? _log;                       // 現在の実行のタイムライン書き込み口

    // チャットと同じ「動作中だと分かる」経過秒つきステータス（実行中ステップの StatusText を刻む）。
    private readonly DispatcherTimer _statusTimer;   // 経過秒の表示を更新する
    private readonly Stopwatch _statusClock = new(); // 現フェーズ開始からの経過時間
    private string _statusPhase = "";                // 経過秒を除いたフェーズ説明

    public ObservableCollection<WorkflowStepViewModel> Steps { get; } = new();

    /// <summary>実行全体のフラットな進捗タイムライン（チャットの「進行状況」と同じ構造化表示）。
    /// 各段＝種別アイコン＋本文＋経過時刻。</summary>
    public TranscriptEntry Activity { get; } = new() { Kind = EntryKind.Activity };

    /// <summary>承認待ちカード（ボタン・差分つき）。タイムラインには「承認待ち」の1段だけ出し、
    /// 操作可能なカードはこちらへ積む（承認/拒否で取り除く）。</summary>
    public ObservableCollection<TranscriptEntry> Approvals { get; } = new();

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

    /// <summary>一度でも実行したか（下部の進捗状況／実行ログ領域は実行後にだけ現れる）。</summary>
    [ObservableProperty] private bool _hasRun;
    public bool IsProgressVisible => HasRun || IsWarmingUp;

    /// <summary>ウォームアップ中か。進捗状況エリアの中身を「ウォームアップ表示」と「実行ログ」で出し分ける。</summary>
    [ObservableProperty] private bool _isWarmingUp;

    /// <summary>実行時にステップへ <c>{{input}}</c> で流し込むワークフロー入力。
    /// 入力バーのテキスト欄／「ファイルから読込」ボタンから設定する。</summary>
    [ObservableProperty] private string _runInput = "";

    /// <summary>ワークフロー全体の最終出力（最後のステップの出力）。実行ログとは別に表示する。</summary>
    [ObservableProperty] private string _finalOutput = "";
    public bool HasFinalOutput => !string.IsNullOrWhiteSpace(FinalOutput);
    partial void OnFinalOutputChanged(string value) => OnPropertyChanged(nameof(HasFinalOutput));

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
        AiSettings settings,
        WorkflowToolRunner toolRunner)
    {
        _orchestrator = orchestrator;
        _approval = approval;
        _store = store;
        _warmup = warmup;
        _settings = settings;
        _toolRunner = toolRunner;

        _approval.ApprovalRequested += OnApprovalRequested;
        _store.Changed += () => Dispatch(RefreshSavedWorkflows);

        _warmupTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _warmupTimer.Tick += (_, _) => RenderWarmupStatus();
        _warmup.StateChanged += OnWarmupStateChanged;
        IsWarmingUp = _warmup.IsWarmingUp;
        if (IsWarmingUp)
        {
            _warmupTimer.Start();
            RenderWarmupStatus();
        }

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) => RenderStepStatus();

        // 「ステップを追加」パレットは先頭カテゴリを開いた状態で出す。
        _selectedStepCategory = StepLibrary.Count > 0 ? StepLibrary[0] : null;

        // ステップはユーザーが明示的に追加するまで作らない。
    }

    /// <summary>ウォームアップ状態の変化を、ワークフロー画面の進捗領域へ反映する。</summary>
    private void OnWarmupStateChanged() => Dispatch(() =>
    {
        RunCommand.NotifyCanExecuteChanged();
        IsWarmingUp = _warmup.IsWarmingUp;
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
        if (!_warmup.IsWarmingUp || _warmup.WarmupStartedAt is not { } startedAt) return;
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

    partial void OnHasRunChanged(bool value) => OnPropertyChanged(nameof(IsProgressVisible));

    partial void OnIsWarmingUpChanged(bool value) => OnPropertyChanged(nameof(IsProgressVisible));

    // ===== 実行中ステップの経過秒つきステータス（チャットの SetStatus と同等） =====

    /// <summary>実行中ステップのフェーズを切り替え、経過秒の表示更新を開始する。</summary>
    private void SetStepStatus(string phase)
    {
        _statusPhase = phase;
        if (!_statusClock.IsRunning) _statusClock.Restart();
        if (!_statusTimer.IsEnabled) _statusTimer.Start();
        RenderStepStatus();
    }

    /// <summary>フェーズ文言に経過秒を付けて、実行中ステップの StatusText を更新する
    /// （実行ログ内のそのステップ見出しに、ライブの「今どこ」がそのまま出る）。</summary>
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

    /// <summary>承認要求を承認カード一覧へ積む。ワークフロー実行中以外は何もしない
    /// （チャット側 <see cref="AiBarViewModel.OnApprovalRequested"/> と排他にして二重処理を防ぐ）。
    /// 承認/拒否が決着したらカードを取り除く（タイムラインには「承認待ち」の1段が残る）。</summary>
    private void OnApprovalRequested(ApprovalContext ctx)
    {
        if (!IsRunning) return;
        var entry = new TranscriptEntry
        {
            Kind = EntryKind.Approval,
            Header = $"承認が必要: {ctx.ToolName}",
            Text = ctx.Summary,
        };
        if (TranscriptFormatting.ContainsDiff(ctx.Summary))
            entry.SetDiff(ctx.Summary);
        entry.BindApproval(ctx.Completion);
        Approvals.Add(entry);
        ctx.Completion.Task.ContinueWith(_ => Dispatch(() => Approvals.Remove(entry)),
            TaskScheduler.Default);
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
            if (e.PropertyName is nameof(WorkflowStepViewModel.Title)
                or nameof(WorkflowStepViewModel.Prompt)
                or nameof(WorkflowStepViewModel.Kind)
                or nameof(WorkflowStepViewModel.Content)
                or nameof(WorkflowStepViewModel.Pattern)
                or nameof(WorkflowStepViewModel.IsRegex))
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

    /// <summary>ワークフロー画面を開くときの一覧更新。未選択または選択中IDが消えている場合は先頭を読み込む。</summary>
    public void RefreshSavedWorkflowsAndSelectFirstIfNeeded()
    {
        RefreshSavedWorkflows();
        if (SavedWorkflows.Count == 0) return;
        if (_currentId is not null && SavedWorkflows.Any(s => s.Id == _currentId)) return;

        LoadWorkflow(SavedWorkflows[0]);
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
            HasRun = false;
            FinalOutput = "";
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
        LoadInto(wf);
    }

    private void LoadInto(Workflow wf)
    {
        _suppressChangeTracking = true;
        try
        {
            _currentId = wf.Id;
            OnPropertyChanged(nameof(CurrentWorkflowId));
            Name = wf.Name;
            Steps.Clear();
            foreach (var s in wf.Steps)
            {
                // 保存済みワークフローの読込時は既定で折りたたむ（新規作成のみ展開で開く）。
                var step = new WorkflowStepViewModel(s) { IsExpanded = false };
                AttachStepChangeTracking(step);
                Steps.Add(step);
            }
            Renumber();
            HasUnsavedChanges = false;
            HasRun = false;
            FinalOutput = "";
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

    /// <summary><c>{{input}}</c> を使うワークフロー（FolderTree／エディタのコンテキストメニュー候補）の一覧。</summary>
    public IReadOnlyList<WorkflowSummary> ListInputWorkflows() => _store.ListInputWorkflows();

    /// <summary>指定IDのワークフローをエディタへ読み込み、<paramref name="input"/> を <c>{{input}}</c> として
    /// 実行する（FolderTree／エディタのコンテキストメニューから呼ばれる）。実行中なら何もしない。
    /// 暖機中でも <see cref="RunAsync"/> 内で完了を待ってから走る。</summary>
    public void RunWithInput(string workflowId, string input)
    {
        if (IsRunning) return;
        var wf = _store.Load(workflowId);
        if (wf is null) return;

        LoadInto(wf);
        RunInput = input ?? "";
        _ = RunAsync();
    }

    private bool CanRun() => !IsRunning && !_warmup.IsWarmingUp;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        HasRun = true;       // 進捗状況／実行ログの領域を出す
        FinalOutput = "";    // 前回の最終出力を消す

        // 実行できる（指示文が空でない）ステップが無ければ何もしない。
        if (!Steps.Any(s => !string.IsNullOrWhiteSpace(s.Prompt)))
        {
            RunStatus = "実行できるステップがありません（指示文を入力してください）。";
            return;
        }

        // 実行ログ（フラットな進捗タイムライン）と承認カードを前回分から作り直す。
        Activity.Steps.Clear();
        Activity.Text = "";
        Approvals.Clear();
        _runClock.Restart();
        _log = new ActivityLog(Activity, () => _runClock.Elapsed);
        _log.Append(ActivityKind.Config, TranscriptFormatting.FormatRunConfig(_settings));

        IsRunning = true;
        NotifyCommandStates();
        _cts = new CancellationTokenSource();
        var sessionId = Guid.NewGuid().ToString("N");
        var outputs = new List<string>();

        // 実行対象は「指示文が空でない」ステップのみ。空ステップは番号の連続性のため出力に空文字を積む。
        try
        {
            // モデル未ロードなら暖機が走る。完了まで進捗状況エリアにウォームアップ表示を出す。
            if (!_warmup.IsReady)
            {
                IsWarmingUp = true;
                _warmupTimer.Start();
                RenderWarmupStatus();
                if (string.IsNullOrEmpty(RunStatus)) RunStatus = "ウォームアップ中…";
            }
            await _warmup.EnsureWarmAsync(_cts.Token);
            _warmupTimer.Stop();
            IsWarmingUp = false;

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
                AppendStepOutput(i + 1, output);
                // ワークフロー出力は途中生成物ではなく、最後のステップの出力だけを別表示する。
                if (i == Steps.Count - 1) FinalOutput = output;
                if (!ok)
                {
                    RunStatus = $"ステップ {i + 1} で停止しました。";
                    return;   // 連鎖を打ち切る
                }
            }

            RunStatus = "完了しました。";
            _log?.Append(ActivityKind.Complete, $"回答が完了しました。合計 {TranscriptFormatting.FormatDuration(_runClock.Elapsed)} かかりました。");
        }
        catch (OperationCanceledException)
        {
            RunStatus = "中断しました。";
            _log?.Append(ActivityKind.Cancel, "ユーザー操作で中断しました。");
            if (_runningStep is not null)
            {
                _runningStep.Status = WorkflowStepStatus.Error;
                _runningStep.StatusText = "中断";
            }
        }
        catch (Exception ex)
        {
            RunStatus = "エラーで停止しました。";
            _log?.Append(ActivityKind.Error, $"例外で停止しました: {ex.Message}");
        }
        finally
        {
            _warmupTimer.Stop();
            IsWarmingUp = false;
            ClearStepStatus();
            _log?.ClearLive();
            _runClock.Stop();
            _runningStep = null;
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
            NotifyCommandStates();
        }
    }

    /// <summary>1ステップを実行し、(最終出力, 成功か) を返す。AI ステップはオーケストレータ経由（LLM）、
    /// それ以外は <see cref="WorkflowToolRunner"/> で決定論実行する。</summary>
    private Task<(string Output, bool Ok)> RunStepAsync(
        WorkflowStepViewModel step, IReadOnlyList<string> outputs, string sessionId, CancellationToken ct)
        => step.Kind == WorkflowStepKind.Ai
            ? RunAiStepAsync(step, outputs, sessionId, ct)
            : RunToolStepAsync(step, outputs, ct);

    /// <summary>AI ステップを実行する。記録は共有のフラットな進捗タイムライン
    /// （<see cref="_log"/>）へ、意味のあるアクションを短い1段ずつ流す（ステップ単位でまとめない）。
    /// 生成中の本文・思考は揮発プレビュー段（保存しない）として見せる。</summary>
    private async Task<(string Output, bool Ok)> RunAiStepAsync(
        WorkflowStepViewModel step, IReadOnlyList<string> outputs, string sessionId, CancellationToken ct)
    {
        var log = _log!;
        step.ResetRun();
        step.Status = WorkflowStepStatus.Running;
        _runningStep = step;

        var prompt = WorkflowPrompt.Resolve(step.Prompt, outputs, RunInput);
        var conversation = new Conversation();
        var clock = Stopwatch.StartNew();

        var rawStream = new StringBuilder();   // 生成中の生テキスト（揮発プレビュー専用）
        var thinkText = new StringBuilder();   // 生成中の思考（揮発プレビュー専用）
        var answer = new StringBuilder();      // 確定した本文（＝このステップの出力）
        var aiCallCount = 0;
        var finalText = "";
        var ok = true;
        var loggedThinking = false;
        var loggedResponse = false;

        SetStepStatus("実行中…");
        log.Append(ActivityKind.Send, "AIに送信しました。応答を待っています。");

        try
        {
            await foreach (var ev in _orchestrator.RunTurnAsync(
                               conversation, prompt, sessionId, ct, turnPreamble: AiSettings.WorkflowTurnPreamble))
            {
                switch (ev)
                {
                    case ThinkingDelta t:
                        if (!loggedThinking)
                        {
                            log.Append(ActivityKind.Think, "モデルが思考を生成しています。");
                            loggedThinking = true;
                        }
                        thinkText.Append(t.Text);
                        var thinkPreview = TranscriptFormatting.StreamPreview(thinkText.ToString());
                        SetStepStatus($"💭 思考中… {thinkPreview}");
                        break;

                    case RawTextDelta raw:
                        if (!loggedResponse)
                        {
                            log.Append(ActivityKind.Response, "回答本文の生成を開始しました。");
                            loggedResponse = true;
                        }
                        rawStream.Append(raw.Text);
                        var preview = rawStream.ToString().Trim();
                        SetStepStatus($"応答生成中… {TranscriptFormatting.StreamPreview(preview)}");
                        log.SetLive(ActivityKind.LiveResponse, preview.Length == 0 ? "" : $"生成中:{Environment.NewLine}{preview}");
                        break;

                    case TextDelta d:
                        if (!loggedResponse)
                        {
                            log.Append(ActivityKind.Response, "回答本文の生成を開始しました。");
                            loggedResponse = true;
                        }
                        thinkText.Clear();
                        answer.Append(d.Text);
                        SetStepStatus("応答生成中…");
                        break;

                    case ToolUseRequested req:
                        thinkText.Clear();
                        rawStream.Clear();
                        answer.Clear();   // ツール呼び出しに添えられた本文はタイムラインに残さない
                        log.ClearLive();
                        var argsPreview = TranscriptFormatting.StreamPreview(req.ToolUse.ArgumentsJson);
                        SetStepStatus($"🔧 {req.ToolUse.Name} を準備中… {argsPreview}");
                        log.Append(ActivityKind.ToolPrepare, $"{req.ToolUse.Name} の呼び出しを準備しています: {argsPreview}");
                        break;

                    case ApprovalRequested ap:
                        SetStepStatus($"⏳ {ap.ToolName} の承認待ち…");
                        log.Append(ActivityKind.Approval, $"{ap.ToolName} の実行承認を待っています。");
                        break;

                    case ToolExecutionStarted started:
                        SetStepStatus($"🔧 {started.ToolUse.Name} を実行中… {TranscriptFormatting.StreamPreview(started.ToolUse.ArgumentsJson)}");
                        log.Append(ActivityKind.ToolRun, $"{started.ToolUse.Name} を実行しています: {TranscriptFormatting.StreamPreview(started.ToolUse.ArgumentsJson)}");
                        break;

                    case ToolExecutionCompleted done:
                        SetStepStatus($"考え中…（直前 {done.ToolUse.Name}: {(done.Result.IsError ? "エラー" : "完了")}）");
                        log.Append(done.Result.IsError ? ActivityKind.ToolError : ActivityKind.ToolDone,
                            $"{done.ToolUse.Name} が完了しました（{(done.Result.IsError ? "エラー" : "成功")}）: {TranscriptFormatting.StreamPreview(done.Result.Content)}。結果を踏まえて次の応答を待っています。");
                        break;

                    case ToolCallParseFailed pf:
                        SetStepStatus("⚠️ ツール呼び出しJSONが不正。AIに再試行させています…");
                        log.Append(ActivityKind.Warn, "ツール呼び出しのJSONが不正でした。モデルの生出力を表示し、正しいJSONで出し直させます。");
                        break;

                    case AgentError:
                        log.ClearLive();
                        log.Append(ActivityKind.Error, $"エラーで停止しました。合計 {TranscriptFormatting.FormatDuration(_runClock.Elapsed)} かかりました。");
                        ok = false;
                        break;

                    case AiUsageReported usage:
                        aiCallCount++;
                        log.Append(ActivityKind.Usage, TranscriptFormatting.FormatUsage(usage, aiCallCount));
                        rawStream.Clear();
                        log.ClearLive();
                        break;

                    case TurnCompleted tc:
                        finalText = tc.FinalText ?? answer.ToString();
                        break;
                }
            }
        }
        finally
        {
            log.ClearLive();
            ClearStepStatus();
        }

        clock.Stop();
        if (ok)
        {
            step.Status = WorkflowStepStatus.Done;
            step.StatusText = $"完了 ({TranscriptFormatting.FormatDuration(clock.Elapsed)})";
        }
        else
        {
            step.Status = WorkflowStepStatus.Error;
            step.StatusText = $"失敗 ({TranscriptFormatting.FormatDuration(clock.Elapsed)})";
        }

        _runningStep = null;
        return (finalText, ok);
    }

    /// <summary>非AIのツールステップ（コマンド/ファイル読込/書込/変換）を LLM を使わず実行し、
    /// (出力, 成功か) を返す。承認が要る種別（Command/WriteFile）は AutoApprove でなければ承認カードを出す。</summary>
    private async Task<(string Output, bool Ok)> RunToolStepAsync(
        WorkflowStepViewModel step, IReadOnlyList<string> outputs, CancellationToken ct)
    {
        var log = _log!;
        step.ResetRun();
        step.Status = WorkflowStepStatus.Running;
        _runningStep = step;

        var model = step.ToModel();
        var primary = WorkflowPrompt.Resolve(model.Prompt, outputs, RunInput);
        var content = WorkflowPrompt.Resolve(model.Content, outputs, RunInput);
        var toolName = WorkflowToolRunner.ToolNameFor(model.Kind);
        var clock = Stopwatch.StartNew();

        try
        {
            // 副作用のある種別は AI パスと同じ承認フローを通す（拒否なら連鎖を打ち切る）。
            if (WorkflowToolRunner.RequiresApproval(model.Kind) && !_settings.Safety.AutoApprove)
            {
                SetStepStatus($"⏳ {toolName} の承認待ち…");
                log.Append(ActivityKind.Approval, $"{toolName} の実行承認を待っています。");
                var summary = _toolRunner.DescribeApproval(model, primary, content);
                var approved = await _approval.RequestApprovalAsync(toolName, summary, ct);
                if (!approved)
                {
                    clock.Stop();
                    step.Status = WorkflowStepStatus.Error;
                    step.StatusText = $"拒否 ({TranscriptFormatting.FormatDuration(clock.Elapsed)})";
                    log.Append(ActivityKind.Warn, $"{toolName} の実行は拒否されました。");
                    return ("", false);
                }
            }

            SetStepStatus($"🔧 {toolName} を実行中… {TranscriptFormatting.StreamPreview(primary)}");
            log.Append(ActivityKind.ToolRun,
                $"{toolName} を実行しています: {TranscriptFormatting.StreamPreview(primary)}");

            var result = await _toolRunner.RunAsync(model, primary, content, ct);
            clock.Stop();

            if (result.Ok)
            {
                step.Status = WorkflowStepStatus.Done;
                step.StatusText = $"完了 ({TranscriptFormatting.FormatDuration(clock.Elapsed)})";
                log.Append(ActivityKind.ToolDone, $"{toolName} が完了しました（成功）: {result.Summary}");
            }
            else
            {
                step.Status = WorkflowStepStatus.Error;
                step.StatusText = $"失敗 ({TranscriptFormatting.FormatDuration(clock.Elapsed)})";
                log.Append(ActivityKind.ToolError, $"{toolName} が失敗しました: {result.Summary}");
            }
            return (result.Output, result.Ok);
        }
        finally
        {
            ClearStepStatus();
            _runningStep = null;
        }
    }

    private void AppendStepOutput(int stepNumber, string output)
    {
        var trimmed = output.Trim();
        if (trimmed.Length == 0)
        {
            _log?.Append(ActivityKind.StepOutput, $"ステップ {stepNumber} の出力: （出力なし）");
            return;
        }

        _log?.Append(
            ActivityKind.StepOutput,
            $"ステップ {stepNumber} の出力: {TranscriptFormatting.OneLine(trimmed, 96)}",
            TranscriptFormatting.Truncate(trimmed));
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
