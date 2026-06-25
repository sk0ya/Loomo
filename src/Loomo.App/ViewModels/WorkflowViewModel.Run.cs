using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>WorkflowViewModel の実行時パート：承認カードの受け口、実行中ステップの経過秒ステータス、
/// ワークフロー実行（AI／ツールステップ）。編集・永続化・プロパティは WorkflowViewModel.cs。</summary>
public sealed partial class WorkflowViewModel
{
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

    // ===== 実行 =====

    /// <summary><c>{{input}}</c> を使うワークフロー（FolderTree／エディタのコンテキストメニュー候補）の一覧。</summary>
    public IReadOnlyList<WorkflowSummary> ListInputWorkflows() => _store.ListInputWorkflows();

    /// <summary>指定IDのワークフローをエディタへ読み込み、<paramref name="input"/> を <c>{{input}}</c> として
    /// 実行する（FolderTree／エディタのコンテキストメニューから呼ばれる）。実行中なら何もしない。
    /// 暖機中でも <see cref="RunAsync"/> 内で完了を待ってから走る。</summary>
    public void RunWithInput(string workflowId, string input)
        => RunWithInput(workflowId, WorkflowRunInput.FromText(input ?? ""));

    public void RunWithInput(string workflowId, WorkflowRunInput input)
    {
        if (IsRunning) return;
        var wf = _store.Load(workflowId);
        if (wf is null) return;

        LoadInto(wf);
        SetRunInput(input);
        _ = RunAsync();
    }

    public void SetRunInput(WorkflowRunInput input)
    {
        _runInputValue = input;
        _suppressRunInputSync = true;
        try
        {
            RunInput = input.Kind == WorkflowRunInputKind.File
                ? input.Path ?? input.PrimaryText
                : input.PrimaryText;
        }
        finally
        {
            _suppressRunInputSync = false;
        }
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
            var runInput = await PrepareRunInputAsync(_runInputValue, _cts.Token);

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
                var (output, ok) = await RunStepAsync(step, outputs, runInput, sessionId, _cts.Token);
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
            IsProgressDetailsExpanded = false;
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

    private async Task<WorkflowRunInput> PrepareRunInputAsync(WorkflowRunInput input, CancellationToken ct)
    {
        if (input.Kind != WorkflowRunInputKind.File || input.Content is not null)
            return input;

        var needsContent = Steps.Any(s =>
            WorkflowPrompt.UsesInputContent(s.Prompt) || WorkflowPrompt.UsesInputContent(s.Content));
        if (!needsContent)
            return input;

        if (string.IsNullOrWhiteSpace(input.Path))
            return input.WithContent("");

        ct.ThrowIfCancellationRequested();
        var content = await _workspace.ReadFileAsync(input.Path);
        ct.ThrowIfCancellationRequested();
        return input.WithContent(content);
    }

    /// <summary>1ステップを実行し、(最終出力, 成功か) を返す。AI ステップはオーケストレータ経由（LLM）、
    /// それ以外は <see cref="WorkflowToolRunner"/> で決定論実行する。</summary>
    private Task<(string Output, bool Ok)> RunStepAsync(
        WorkflowStepViewModel step, IReadOnlyList<string> outputs, WorkflowRunInput runInput, string sessionId, CancellationToken ct)
        => step.Kind == WorkflowStepKind.Ai
            ? RunAiStepAsync(step, outputs, runInput, sessionId, ct)
            : RunToolStepAsync(step, outputs, runInput, ct);

    /// <summary>AI ステップを実行する。記録は共有のフラットな進捗タイムライン
    /// （<see cref="_log"/>）へ、意味のあるアクションを短い1段ずつ流す（ステップ単位でまとめない）。
    /// 生成中の本文・思考は揮発プレビュー段（保存しない）として見せる。</summary>
    private async Task<(string Output, bool Ok)> RunAiStepAsync(
        WorkflowStepViewModel step, IReadOnlyList<string> outputs, WorkflowRunInput runInput, string sessionId, CancellationToken ct)
    {
        var log = _log!;
        step.ResetRun();
        step.Status = WorkflowStepStatus.Running;
        _runningStep = step;

        var prompt = WorkflowPrompt.Resolve(step.Prompt, outputs, runInput);
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
        WorkflowStepViewModel step, IReadOnlyList<string> outputs, WorkflowRunInput runInput, CancellationToken ct)
    {
        var log = _log!;
        step.ResetRun();
        step.Status = WorkflowStepStatus.Running;
        _runningStep = step;

        var model = step.ToModel();
        var primary = WorkflowPrompt.Resolve(model.Prompt, outputs, runInput);
        var content = WorkflowPrompt.Resolve(model.Content, outputs, runInput);
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
}

