using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>下部AIバー（全幅・展開式）の ViewModel。エージェントループを駆動する。</summary>
public sealed partial class AiBarViewModel : ObservableObject
{
    private readonly AgentOrchestrator _orchestrator;
    private readonly Conversation _conversation = new();
    private CancellationTokenSource? _cts;

    public ObservableCollection<TranscriptEntry> Transcript { get; } = new();

    [ObservableProperty] private string _input = "";
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _providerLabel = "Stub";

    public AiBarViewModel(AgentOrchestrator orchestrator, UiApprovalService approval)
    {
        _orchestrator = orchestrator;
        approval.ApprovalRequested += OnApprovalRequested;
    }

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(Input);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Input.Trim();
        Input = "";
        IsExpanded = true;
        IsBusy = true;
        _cts = new CancellationTokenSource();

        Add(EntryKind.User, "あなた", text);
        TranscriptEntry? assistant = null;

        try
        {
            await foreach (var ev in _orchestrator.RunTurnAsync(_conversation, text, _cts.Token))
            {
                switch (ev)
                {
                    case TextDelta delta:
                        assistant ??= Add(EntryKind.Assistant, "🤖 エージェント", "");
                        assistant.AppendText(delta.Text);
                        break;

                    case ToolUseRequested req:
                        assistant = null; // テキストブロックを区切る
                        Add(EntryKind.Tool, $"🔧 {req.ToolUse.Name}", req.ToolUse.ArgumentsJson);
                        break;

                    case ToolExecutionCompleted done:
                        Add(EntryKind.Tool, $"↳ 結果 ({done.ToolUse.Name})", Truncate(done.Result.Content));
                        break;

                    case AgentError err:
                        Add(EntryKind.Error, "⚠️ エラー", err.Message);
                        break;

                    case TurnCompleted:
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Add(EntryKind.Error, "⏹ 中断", "ユーザーにより中断されました。");
        }
        catch (Exception ex)
        {
            Add(EntryKind.Error, "⚠️ 例外", ex.Message);
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

    private void OnApprovalRequested(ApprovalContext ctx)
    {
        var entry = Add(EntryKind.Approval, $"承認が必要: {ctx.ToolName}", ctx.Summary);
        entry.BindApproval(ctx.Completion);
    }

    private TranscriptEntry Add(EntryKind kind, string header, string text)
    {
        var entry = new TranscriptEntry { Kind = kind, Header = header, Text = text };
        Transcript.Add(entry);
        return entry;
    }

    private static string Truncate(string s, int max = 2000)
        => s.Length <= max ? s : s[..max] + $"\n…(+{s.Length - max} 文字)";

    partial void OnInputChanged(string value) => SendCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value) => SendCommand.NotifyCanExecuteChanged();
}
