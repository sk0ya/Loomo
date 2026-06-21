using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// ワークフローモードの ViewModel。ユーザーが手で並べた「単発のAI指示」（ステップ）を順に実行し、
/// 前段の出力を <c>{{1}}</c> 等のプレースホルダで後段へ渡す。エージェントループを回す代わりに、
/// テキストのみステップは1回のAI応答（ツール無し）で確定する。ツール使用ステップはツール込みで実行する。
/// </summary>
public sealed partial class WorkflowViewModel : ObservableObject
{
    private static readonly IReadOnlyList<ToolDefinition> NoTools = Array.Empty<ToolDefinition>();

    private readonly AgentOrchestrator _orchestrator;
    private readonly UiApprovalService _approval;
    private readonly WorkflowStore _store;
    private readonly IAiWarmup _warmup;

    private CancellationTokenSource? _cts;
    private WorkflowStepViewModel? _runningStep;   // 承認カードの差し込み先
    private string? _currentId;                     // 読込済みワークフローのID（保存時に上書き）

    public ObservableCollection<WorkflowStepViewModel> Steps { get; } = new();

    /// <summary>読込ドロップダウンに出す保存済みワークフロー一覧。</summary>
    public ObservableCollection<WorkflowSummary> SavedWorkflows { get; } = new();

    [ObservableProperty] private string _name = "新しいワークフロー";
    [ObservableProperty] private bool _isRunning;

    /// <summary>実行中の全体状況（「ステップ2/3 を実行中…」等）。</summary>
    [ObservableProperty] private string _runStatus = "";

    public WorkflowViewModel(
        AgentOrchestrator orchestrator,
        UiApprovalService approval,
        WorkflowStore store,
        IAiWarmup warmup)
    {
        _orchestrator = orchestrator;
        _approval = approval;
        _store = store;
        _warmup = warmup;

        _approval.ApprovalRequested += OnApprovalRequested;
        _store.Changed += () => Dispatch(RefreshSavedWorkflows);

        // 初期状態として空のステップを1つ用意する。
        AddStep();
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
        Steps.Add(new WorkflowStepViewModel());
        Renumber();
    }

    private bool CanEdit() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RemoveStep(WorkflowStepViewModel? step)
    {
        if (step is null) return;
        Steps.Remove(step);
        Renumber();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void MoveUp(WorkflowStepViewModel? step)
    {
        if (step is null) return;
        var i = Steps.IndexOf(step);
        if (i > 0) { Steps.Move(i, i - 1); Renumber(); }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void MoveDown(WorkflowStepViewModel? step)
    {
        if (step is null) return;
        var i = Steps.IndexOf(step);
        if (i >= 0 && i < Steps.Count - 1) { Steps.Move(i, i + 1); Renumber(); }
    }

    /// <summary>並び順（Index）と参照トークンを振り直す。</summary>
    private void Renumber()
    {
        for (var i = 0; i < Steps.Count; i++)
            Steps[i].Index = i + 1;
    }

    // ===== 永続化 =====

    public void RefreshSavedWorkflows()
    {
        SavedWorkflows.Clear();
        foreach (var s in _store.List()) SavedWorkflows.Add(s);
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Save()
    {
        var wf = new Workflow
        {
            Id = _currentId,
            Name = string.IsNullOrWhiteSpace(Name) ? "(無題)" : Name.Trim(),
            Steps = Steps.Select(s => s.ToModel()).ToList(),
        };
        _currentId = _store.Save(wf);
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void NewWorkflow()
    {
        _currentId = null;
        Name = "新しいワークフロー";
        Steps.Clear();
        AddStep();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void LoadWorkflow(WorkflowSummary? summary)
    {
        if (summary is null) return;
        var wf = _store.Load(summary.Id);
        if (wf is null) return;

        _currentId = wf.Id;
        Name = wf.Name;
        Steps.Clear();
        foreach (var s in wf.Steps) Steps.Add(new WorkflowStepViewModel(s));
        if (Steps.Count == 0) AddStep();
        Renumber();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void DeleteWorkflow(WorkflowSummary? summary)
    {
        if (summary is null) return;
        _store.Delete(summary.Id);
        if (_currentId == summary.Id) _currentId = null;
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
            await _warmup.EnsureWarmAsync(_cts.Token);

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
        step.StatusText = "実行中…";
        step.IsLogCollapsed = false;   // 実行中は進捗を見せる
        _runningStep = step;

        var prompt = WorkflowPrompt.Resolve(step.Prompt, outputs);
        var conversation = new Conversation();
        var clock = Stopwatch.StartNew();
        var toolDefs = step.UseTools ? null : NoTools;

        TranscriptEntry? assistant = null;
        TranscriptEntry? thinking = null;
        var finalText = "";
        var ok = true;

        await foreach (var ev in _orchestrator.RunTurnAsync(
                           conversation, prompt, sessionId, ct, toolDefinitionsOverride: toolDefs))
        {
            switch (ev)
            {
                case ThinkingDelta t:
                    thinking ??= AddLog(step, EntryKind.Thinking, "💭 思考", "");
                    thinking.AppendText(t.Text);
                    break;

                case TextDelta d:
                    thinking = null;
                    assistant ??= AddLog(step, EntryKind.Assistant, "エージェント", "");
                    assistant.AppendText(d.Text);
                    break;

                case ToolUseRequested req:
                    thinking = null;
                    var narration = assistant?.Text;
                    if (assistant is not null) { step.Log.Remove(assistant); assistant = null; }
                    AddLog(step, EntryKind.Tool,
                        TranscriptFormatting.ToolUseHeader(req.ToolUse.Name, req.ToolUse.ArgumentsJson),
                        TranscriptFormatting.ComposeToolCard(narration, req.ToolUse.ArgumentsJson, req.ToolUse.RawJson),
                        collapsed: true);
                    break;

                case ApprovalRequested ap:
                    step.StatusText = $"⏳ {ap.ToolName} の承認待ち…";
                    break;

                case ToolExecutionStarted started:
                    step.StatusText = $"🔧 {started.ToolUse.Name} を実行中…";
                    break;

                case ToolExecutionCompleted done:
                    step.StatusText = "実行中…";
                    AddLog(step, EntryKind.Tool, $"↳ 結果 ({done.ToolUse.Name})",
                        TranscriptFormatting.Truncate(done.Result.Content), collapsed: true);
                    break;

                case ToolCallParseFailed pf:
                    if (assistant is not null) { step.Log.Remove(assistant); assistant = null; }
                    AddLog(step, EntryKind.Error, "⚠️ 不正なツール出力（再試行）", pf.RawText);
                    break;

                case AgentError err:
                    AddLog(step, EntryKind.Error, "⚠️ エラー", err.Message);
                    ok = false;
                    break;

                case TurnCompleted tc:
                    finalText = tc.FinalText ?? assistant?.Text ?? "";
                    break;
            }
        }

        clock.Stop();
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
        RemoveStepCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        NewWorkflowCommand.NotifyCanExecuteChanged();
        LoadWorkflowCommand.NotifyCanExecuteChanged();
        DeleteWorkflowCommand.NotifyCanExecuteChanged();
    }
}
