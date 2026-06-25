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
using sk0ya.Loomo.Core.Abstractions;
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
    private readonly IWorkspaceService _workspace;

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

    /// <summary>進行状況の詳細タイムラインを開くか。通常は閉じ、実行中だけ自動で開く。</summary>
    [ObservableProperty] private bool _isProgressDetailsExpanded;

    /// <summary>ウォームアップ中か。進捗状況エリアの中身を「ウォームアップ表示」と「実行ログ」で出し分ける。</summary>
    [ObservableProperty] private bool _isWarmingUp;

    /// <summary>実行時にステップへ <c>{{input}}</c> で流し込むワークフロー入力。
    /// 入力バーのテキスト欄／「ファイルから読込」ボタンから設定する。</summary>
    [ObservableProperty] private string _runInput = "";
    private WorkflowRunInput _runInputValue = WorkflowRunInput.FromText("");
    private bool _suppressRunInputSync;

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
        WorkflowToolRunner toolRunner,
        IWorkspaceService workspace)
    {
        _orchestrator = orchestrator;
        _approval = approval;
        _store = store;
        _warmup = warmup;
        _settings = settings;
        _toolRunner = toolRunner;
        _workspace = workspace;

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

    partial void OnIsRunningChanged(bool value)
    {
        if (value)
            IsProgressDetailsExpanded = true;
    }

    partial void OnRunInputChanged(string value)
    {
        if (!_suppressRunInputSync)
            _runInputValue = WorkflowRunInput.FromText(value ?? "");
    }

    private static void Dispatch(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app is null || app.Dispatcher.CheckAccess()) action();
        else app.Dispatcher.BeginInvoke(action);
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

    public void RenameWorkflow(WorkflowSummary? summary, string? newName)
    {
        if (summary is null || string.IsNullOrWhiteSpace(newName) || IsRunning) return;
        var trimmed = newName.Trim();
        if (trimmed == summary.Name) return;

        var wf = _store.Load(summary.Id);
        if (wf is null) return;
        wf.Name = trimmed;
        _store.Save(wf);

        if (_currentId == summary.Id)
        {
            _suppressChangeTracking = true;
            try
            {
                Name = trimmed;
                HasUnsavedChanges = false;
            }
            finally
            {
                _suppressChangeTracking = false;
            }
            OnPropertyChanged(nameof(SaveStatus));
        }
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
