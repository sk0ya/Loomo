using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>入力欄で「/」から呼び出すスラッシュコマンド1件（補完候補に出す）。</summary>
public sealed record ChatCommand(string Name, string Description);

/// <summary>下部AIバー（全幅・展開式）の ViewModel。エージェントループを駆動する。</summary>
public sealed partial class AiBarViewModel : ObservableObject
{
    private readonly AgentOrchestrator _orchestrator;
    private readonly AiSettings _settings;
    private readonly AiSettingsStore _store;
    private readonly ConversationStore _sessions;
    private Conversation _conversation = new();
    private string? _currentSessionId;
    private string? _lastClosedSessionId;   // /resume で復元する直前に閉じたセッション
    private bool _suppressSuggestions;       // プログラムからの Input 書換時に補完を抑止
    private CancellationTokenSource? _cts;

    public ObservableCollection<TranscriptEntry> Transcript { get; } = new();

    /// <summary>利用可能なスラッシュコマンド一覧。</summary>
    public static IReadOnlyList<ChatCommand> AllCommands { get; } = new[]
    {
        new ChatCommand("/clear", "現在のセッションを閉じて新規開始"),
        new ChatCommand("/resume", "直前に閉じたセッションを復元"),
    };

    /// <summary>「/」入力中に表示するコマンド補完候補。</summary>
    public ObservableCollection<ChatCommand> CommandSuggestions { get; } = new();

    /// <summary>コマンド補完ポップアップの開閉。</summary>
    [ObservableProperty] private bool _isCommandPopupOpen;

    /// <summary>補完候補リストでハイライト中の行（-1 で無選択）。</summary>
    [ObservableProperty] private int _selectedCommandIndex = -1;

    [ObservableProperty] private string _input = "";
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _providerLabel;

    /// <summary>タイトルバーのクイック切替に出すプロバイダ一覧。</summary>
    public IReadOnlyList<AiProvider> Providers { get; } =
        (AiProvider[])Enum.GetValues(typeof(AiProvider));

    /// <summary>タイトルバーで選択中のプロバイダ。変更すると即時に切り替えて永続化する。</summary>
    [ObservableProperty] private AiProvider _selectedProvider;

    /// <summary>タイトルバーからプロバイダが切り替えられたとき、選択中のプロバイダを通知する
    /// （設定パネルの表示を追従させるために使う）。</summary>
    public event Action<AiProvider>? ProviderSwitched;

    public AiBarViewModel(
        AgentOrchestrator orchestrator,
        UiApprovalService approval,
        AiSettings settings,
        AiSettingsStore store,
        ConversationStore sessions)
    {
        _orchestrator = orchestrator;
        _settings = settings;
        _store = store;
        _sessions = sessions;
        _providerLabel = settings.Provider.ToString();
        _selectedProvider = settings.Provider;
        approval.ApprovalRequested += OnApprovalRequested;
    }

    /// <summary>設定変更後に現在のプロバイダ表示・選択を更新する（設定パネル → タイトルバーの同期）。</summary>
    public void RefreshProviderLabel()
    {
        ProviderLabel = _settings.Provider.ToString();
        SelectedProvider = _settings.Provider;
    }

    /// <summary>タイトルバーのコンボでプロバイダを切り替えたら即時に反映・永続化する。
    /// <see cref="AiClientFactory.ResolveCurrent"/> は毎ターン設定を読むため、保存だけで次の応答から有効になる。</summary>
    partial void OnSelectedProviderChanged(AiProvider value)
    {
        if (value == _settings.Provider) return; // 同期由来の再設定（設定パネルからの保存など）は無視

        _settings.Provider = value;
        ProviderLabel = value.ToString();
        // 保存に失敗しても切替自体（メモリ上の _settings.Provider）は有効だが、永続化されない旨を
        // 通知する（無通知だと保存済みと誤認され、次回起動で旧プロバイダに戻る）。
        try { _store.Save(_settings); }
        catch (Exception ex)
        {
            Add(EntryKind.Error, "⚠️ 設定の保存に失敗",
                $"プロバイダを {value} に切り替えましたが保存できませんでした（次回起動時は元に戻ります）: {ex.Message}");
        }
        ProviderSwitched?.Invoke(value); // 設定パネルの選択を追従させる
    }

    /// <summary>新しい空のセッションを開始する（履歴一覧の「新規」から呼ばれる）。</summary>
    public void StartNewSession()
    {
        _conversation = new Conversation();
        _currentSessionId = null;
        Transcript.Clear();
    }

    /// <summary>現在のセッションを閉じて新規開始する（/clear）。直前のIDは /resume 用に控える。</summary>
    public void ClearSession()
    {
        if (_currentSessionId is not null && _conversation.Messages.Count > 0)
            _lastClosedSessionId = _currentSessionId;

        StartNewSession();
        IsExpanded = true;
        Add(EntryKind.Info, "🧹 セッションを閉じました",
            _lastClosedSessionId is not null
                ? "/resume で直前のセッションを復元できます。"
                : "新しいセッションを開始しました。");
    }

    /// <summary>直前に閉じたセッション（無ければ最新の保存セッション）を復元する（/resume）。</summary>
    public void ResumeLastSession()
    {
        var id = _lastClosedSessionId ?? _sessions.List().FirstOrDefault()?.Id;
        if (id is null)
        {
            IsExpanded = true;
            Add(EntryKind.Error, "復元できるセッションがありません", "保存済みのセッションが見つかりませんでした。");
            return;
        }

        var session = _sessions.Load(id);
        if (session is null)
        {
            IsExpanded = true;
            Add(EntryKind.Error, "セッションの復元に失敗しました", id);
            return;
        }

        RestoreSession(session);
        _lastClosedSessionId = null;
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

        // スラッシュコマンドなら AI へ送らず即実行する。
        if (TryRunChatCommand(text))
        {
            SetInput("");
            CloseCommandPopup();
            return;
        }

        Input = "";
        CloseCommandPopup();
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

    // ===== スラッシュコマンド =====

    /// <summary>入力がスラッシュコマンドなら実行して true。未知の「/...」は通常送信に委ねる。</summary>
    private bool TryRunChatCommand(string text)
    {
        if (string.IsNullOrEmpty(text) || text[0] != '/') return false;
        var name = text.Split(' ', 2)[0].ToLowerInvariant();
        switch (name)
        {
            case "/clear": ClearSession(); return true;
            case "/resume": ResumeLastSession(); return true;
            default: return false; // 既知コマンドでなければ通常メッセージとして送る
        }
    }

    /// <summary>入力内容に応じてコマンド補完候補を更新する。</summary>
    private void UpdateCommandSuggestions(string value)
    {
        // 「/」始まりで、まだ空白を含まない（=コマンド名入力中）のときだけ候補を出す。
        if (value.Length > 0 && value[0] == '/' && !value.Contains(' '))
        {
            var matches = AllCommands
                .Where(c => c.Name.StartsWith(value, StringComparison.OrdinalIgnoreCase))
                .ToList();

            CommandSuggestions.Clear();
            foreach (var c in matches) CommandSuggestions.Add(c);
            IsCommandPopupOpen = matches.Count > 0;
            SelectedCommandIndex = matches.Count > 0 ? 0 : -1;
        }
        else
        {
            CloseCommandPopup();
        }
    }

    public void CloseCommandPopup()
    {
        IsCommandPopupOpen = false;
        CommandSuggestions.Clear();
        SelectedCommandIndex = -1;
    }

    /// <summary>補完候補の選択を上下に移動する（端でラップ）。</summary>
    public void MoveCommandSelection(int delta)
    {
        if (CommandSuggestions.Count == 0) return;
        var i = SelectedCommandIndex + delta;
        if (i < 0) i = CommandSuggestions.Count - 1;
        if (i >= CommandSuggestions.Count) i = 0;
        SelectedCommandIndex = i;
    }

    private ChatCommand? CurrentSuggestion()
        => SelectedCommandIndex >= 0 && SelectedCommandIndex < CommandSuggestions.Count
            ? CommandSuggestions[SelectedCommandIndex]
            : null;

    /// <summary>選択中の候補で入力を補完する（Tab）。実行はしない。補完したら true。</summary>
    public bool CompleteSelectedCommand()
    {
        if (CurrentSuggestion() is not { } cmd) return false;
        SetInput(cmd.Name + " ");
        CloseCommandPopup();
        return true;
    }

    /// <summary>選択中の候補を確定して実行する（Enter / クリック）。実行したら true。</summary>
    public bool AcceptAndRunSelectedCommand()
    {
        if (CurrentSuggestion() is not { } cmd) return false;
        CloseCommandPopup();
        SetInput(cmd.Name);
        if (SendCommand.CanExecute(null)) SendCommand.Execute(null);
        return true;
    }

    /// <summary>補完を発火させずに入力欄を書き換える。</summary>
    private void SetInput(string value)
    {
        _suppressSuggestions = true;
        Input = value;
        _suppressSuggestions = false;
    }

    partial void OnInputChanged(string value)
    {
        SendCommand.NotifyCanExecuteChanged();
        if (!_suppressSuggestions) UpdateCommandSuggestions(value);
    }

    partial void OnIsBusyChanged(bool value) => SendCommand.NotifyCanExecuteChanged();
}
