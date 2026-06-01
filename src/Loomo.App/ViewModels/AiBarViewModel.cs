using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Ai;
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
    private readonly AiSettings _settings;
    private readonly ConversationStore _sessions;
    private Conversation _conversation = new();
    private string? _currentSessionId;
    private CancellationTokenSource? _cts;

    public ObservableCollection<TranscriptEntry> Transcript { get; } = new();

    [ObservableProperty] private string _input = "";
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _providerLabel;

    public AiBarViewModel(
        AgentOrchestrator orchestrator,
        UiApprovalService approval,
        AiSettings settings,
        ConversationStore sessions)
    {
        _orchestrator = orchestrator;
        _settings = settings;
        _sessions = sessions;
        _providerLabel = settings.Provider.ToString();
        approval.ApprovalRequested += OnApprovalRequested;
    }

    /// <summary>設定変更後に現在のプロバイダ表示を更新する。</summary>
    public void RefreshProviderLabel() => ProviderLabel = _settings.Provider.ToString();

    /// <summary>新しい空のセッションを開始する（履歴一覧の「新規」から呼ばれる）。</summary>
    public void StartNewSession()
    {
        _conversation = new Conversation();
        _currentSessionId = null;
        Transcript.Clear();
    }

    /// <summary>保存済みセッションを復元してトランスクリプトを再構築する。</summary>
    public void RestoreSession(LoadedSession session)
    {
        _conversation = session.Conversation;
        _currentSessionId = session.Id;
        RebuildTranscript();
        IsExpanded = true;
    }

    /// <summary>会話メッセージからトランスクリプト表示を組み直す（承認カード等の動的要素は除く）。</summary>
    private void RebuildTranscript()
    {
        Transcript.Clear();
        foreach (var m in _conversation.Messages)
        {
            switch (m.Role)
            {
                case ChatRole.User:
                    Add(EntryKind.User, "あなた", m.Text ?? "");
                    break;

                case ChatRole.Assistant:
                    if (!string.IsNullOrWhiteSpace(m.Text))
                        Add(EntryKind.Assistant, "🤖 エージェント", m.Text);
                    foreach (var use in m.ToolUses)
                        Add(EntryKind.Tool, $"🔧 {use.Name}", use.ArgumentsJson);
                    break;

                case ChatRole.Tool:
                    foreach (var r in m.ToolResults)
                        Add(EntryKind.Tool, "↳ 結果", Truncate(r.Content));
                    break;
            }
        }
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

        // トレース（§20）と保存が同じIDを共有するよう、ターン開始前にセッションIDを確定する。
        _currentSessionId ??= Guid.NewGuid().ToString("N");

        Add(EntryKind.User, "あなた", text);
        TranscriptEntry? assistant = null;

        try
        {
            await foreach (var ev in _orchestrator.RunTurnAsync(_conversation, text, _currentSessionId, _cts.Token))
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

            // ターン終了時にセッションを自動保存（新規なら採番）
            try { _currentSessionId = _sessions.Save(_currentSessionId, _conversation); }
            catch { /* 保存失敗は会話を妨げない */ }
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private void OnApprovalRequested(ApprovalContext ctx)
    {
        var entry = Add(EntryKind.Approval, $"承認が必要: {ctx.ToolName}", ctx.Summary);
        if (ctx.ToolName == "propose_edit")
            entry.SetDiff(ctx.Summary);   // サマリを色付き差分へ展開
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
