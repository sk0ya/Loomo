using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
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
    private readonly ConversationStore _sessions;
    private readonly PromptHistoryStore _historyStore;
    private Conversation _conversation = new();
    private string? _currentSessionId;
    private string? _lastClosedSessionId;   // /resume で復元する直前に閉じたセッション
    private bool _suppressSuggestions;       // プログラムからの Input 書換時に補完を抑止
    private CancellationTokenSource? _cts;

    private readonly List<string> _history = new();   // 送信済みプロンプト（古い→新しい）
    private int _historyCursor = -1;                   // 履歴ナビ位置（-1 = 未ナビ／編集中の下書き）
    private string _historyDraft = "";                 // ナビ開始前に編集していた内容

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

    /// <summary>処理中に「いま何をしているか」を示すステータス文言（考え中／ツール実行中／承認待ち…）。
    /// 経過秒を併記して、止まっているのではなく動作中だと分かるようにする。</summary>
    [ObservableProperty] private string _statusText = "";

    private string _statusPhase = "";                 // 経過秒を除いたフェーズ説明
    private readonly Stopwatch _statusClock = new();   // 処理開始からの経過時間
    private readonly DispatcherTimer _statusTimer;     // 経過秒の表示を更新する

    public AiBarViewModel(
        AgentOrchestrator orchestrator,
        UiApprovalService approval,
        AiSettings settings,
        ConversationStore sessions,
        PromptHistoryStore historyStore)
    {
        _orchestrator = orchestrator;
        _settings = settings;
        _sessions = sessions;
        _historyStore = historyStore;
        _history.AddRange(historyStore.Load());   // 前回までの送信履歴を引き継ぐ
        _providerLabel = settings.Provider.ToString();
        approval.ApprovalRequested += OnApprovalRequested;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) => RenderStatus();
    }

    /// <summary>現在のフェーズに切り替え、経過秒の表示更新を開始する。</summary>
    private void SetStatus(string phase)
    {
        _statusPhase = phase;
        if (!_statusClock.IsRunning) _statusClock.Restart();
        if (!_statusTimer.IsEnabled) _statusTimer.Start();
        RenderStatus();
    }

    /// <summary>フェーズ文言に経過秒を付けて StatusText を更新する。</summary>
    private void RenderStatus()
    {
        if (string.IsNullOrEmpty(_statusPhase)) { StatusText = ""; return; }
        StatusText = $"{_statusPhase} （{_statusClock.Elapsed.TotalSeconds:0}秒）";
    }

    /// <summary>処理終了：タイマー・経過時計を止めてステータスを消す。</summary>
    private void ClearStatus()
    {
        _statusTimer.Stop();
        _statusClock.Reset();
        _statusPhase = "";
        StatusText = "";
    }

    /// <summary>設定変更後に現在のプロバイダ表示・選択を更新する（設定パネル → タイトルバーの同期）。</summary>
    public void RefreshProviderLabel()
    {
        ProviderLabel = _settings.Provider.ToString();
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
        PushHistory(text);

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
        SetStatus("考え中…");

        // トレース（§20）と保存が同じIDを共有するよう、ターン開始前にセッションIDを確定する。
        _currentSessionId ??= Guid.NewGuid().ToString("N");

        Add(EntryKind.User, "あなた", text);
        TranscriptEntry? assistant = null;
        TranscriptEntry? thinking = null;
        Stopwatch? assistantClock = null;
        Stopwatch? thinkingClock = null;

        try
        {
            await foreach (var ev in _orchestrator.RunTurnAsync(_conversation, text, _currentSessionId, _cts.Token))
            {
                switch (ev)
                {
                    case ThinkingDelta think:
                        SetStatus("💭 思考中…");
                        if (thinking is null)
                        {
                            thinking = Add(EntryKind.Thinking, "💭 思考", "");
                            thinkingClock = Stopwatch.StartNew();
                        }
                        thinking.AppendText(think.Text);
                        break;

                    case TextDelta delta:
                        SetStatus("応答を生成中…");
                        FinishTimedEntry(ref thinking, ref thinkingClock, "💭 思考");
                        thinking = null; // 本文に入ったので思考ブロックを区切る
                        if (assistant is null)
                        {
                            assistant = Add(EntryKind.Assistant, "🤖 エージェント", "");
                            assistantClock = Stopwatch.StartNew();
                        }
                        assistant.AppendText(delta.Text);
                        break;

                    case ToolUseRequested req:
                        FinishTimedEntry(ref assistant, ref assistantClock, "🤖 エージェント");
                        FinishTimedEntry(ref thinking, ref thinkingClock, "💭 思考");
                        assistant = null; // テキストブロックを区切る
                        thinking = null;
                        SetStatus($"🔧 {req.ToolUse.Name} を準備中…");
                        Add(EntryKind.Tool, $"🔧 {req.ToolUse.Name}", req.ToolUse.ArgumentsJson);
                        break;

                    case ApprovalRequested approval:
                        SetStatus($"⏳ {approval.ToolName} の承認待ち…");
                        break;

                    case ToolExecutionStarted started:
                        SetStatus($"🔧 {started.ToolUse.Name} を実行中…");
                        break;

                    case ToolExecutionCompleted done:
                        SetStatus("考え中…"); // ツール結果を踏まえてAIが再応答する
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
            FinishTimedEntry(ref assistant, ref assistantClock, "🤖 エージェント");
            FinishTimedEntry(ref thinking, ref thinkingClock, "💭 思考");
            IsBusy = false;
            ClearStatus();
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
        if (ContainsDiff(ctx.Summary))
            entry.SetDiff(ctx.Summary);   // サマリを色付き差分へ展開
        entry.BindApproval(ctx.Completion);
    }

    /// <summary>承認サマリが統合差分（+/- 接頭辞付きの行）を含むか。編集系ツールの「ヘッダ＋差分」要約は
    /// 含み、引数不正などのエラー要約は含まないので、ツール名のハードコードより堅牢に差分カードを出し分けられる。</summary>
    private static bool ContainsDiff(string summary)
    {
        foreach (var line in summary.AsSpan().EnumerateLines())
            if (line.Length > 0 && (line[0] == '+' || line[0] == '-'))
                return true;
        return false;
    }

    private TranscriptEntry Add(EntryKind kind, string header, string text)
    {
        var entry = new TranscriptEntry { Kind = kind, Header = header, Text = text };
        Transcript.Add(entry);
        return entry;
    }

    private static string Truncate(string s, int max = 2000)
        => s.Length <= max ? s : s[..max] + $"\n…(+{s.Length - max} 文字)";

    private static void FinishTimedEntry(ref TranscriptEntry? entry, ref Stopwatch? clock, string baseHeader)
    {
        if (entry is null || clock is null) return;
        clock.Stop();
        entry.Header = $"{baseHeader} ({FormatDuration(clock.Elapsed)})";
        clock = null;
    }

    private static string FormatDuration(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 1)
            return $"{elapsed.TotalMilliseconds:0} ms";
        if (elapsed.TotalMinutes < 1)
            return $"{elapsed.TotalSeconds:0.0} 秒";
        return $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒";
    }

    // ===== 入力履歴（↑/↓ で呼び出し） =====

    /// <summary>送信したプロンプトを履歴に積む（直前と同一なら積まない）。</summary>
    private void PushHistory(string text)
    {
        _historyCursor = -1;
        _historyDraft = "";
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_history.Count > 0 && _history[^1] == text) return;
        _history.Add(text);
        // メモリ上の履歴もファイルと同じ上限で切り詰める（無制限な肥大を防ぐ）。
        if (_history.Count > _historyStore.MaxEntries)
            _history.RemoveRange(0, _history.Count - _historyStore.MaxEntries);
        try { _historyStore.Save(_history); } catch { /* 保存失敗は会話を妨げない */ }
    }

    /// <summary>↑：ひとつ前の入力履歴を呼び出す。履歴があれば true（キーを消費）。</summary>
    public bool RecallPreviousHistory()
    {
        if (_history.Count == 0) return false;
        if (_historyCursor < 0)
        {
            _historyDraft = Input;            // ナビ開始：いまの下書きを退避
            _historyCursor = _history.Count;
        }
        if (_historyCursor == 0) return true; // 既に最古：消費はするが内容は変えない
        _historyCursor--;
        SetInput(_history[_historyCursor]);
        return true;
    }

    /// <summary>↓：ひとつ後の入力履歴（末尾を超えたら下書きへ戻す）。ナビ中なら true。</summary>
    public bool RecallNextHistory()
    {
        if (_historyCursor < 0) return false; // ナビ中でなければ素通し
        if (_historyCursor >= _history.Count - 1)
        {
            _historyCursor = -1;
            SetInput(_historyDraft);          // 末尾を超えたら編集中の下書きへ
            return true;
        }
        _historyCursor++;
        SetInput(_history[_historyCursor]);
        return true;
    }

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
        if (!_suppressSuggestions)
        {
            _historyCursor = -1;   // 手入力したら履歴ナビをリセット
            UpdateCommandSuggestions(value);
        }
    }

    partial void OnIsBusyChanged(bool value) => SendCommand.NotifyCanExecuteChanged();
}
