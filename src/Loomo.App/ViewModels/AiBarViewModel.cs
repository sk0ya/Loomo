using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Ai.Clients;
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
                    // ライブ表示と同様、user 発言の直後に当該ターンの進行状況を再構築する。
                    if (!string.IsNullOrEmpty(m.ProgressLog))
                        Add(EntryKind.Activity, "進行状況", m.ProgressLog);
                    break;

                case ChatRole.Assistant:
                    if (m.ToolUses.Count > 0)
                    {
                        // ライブ表示と同様、本文はツールカードへ畳み（最初のツールにのみ併記）独立エントリにしない。
                        for (var i = 0; i < m.ToolUses.Count; i++)
                        {
                            var use = m.ToolUses[i];
                            var text = i == 0
                                ? ComposeToolCard(m.Text, use.ArgumentsJson, use.RawJson)
                                : ComposeToolCard(null, use.ArgumentsJson, use.RawJson);
                            Add(EntryKind.Tool, $"🔧 {use.Name}", text);
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(m.Text))
                    {
                        Add(EntryKind.Assistant, "🤖 エージェント", m.Text);
                    }
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
        var turnClock = Stopwatch.StartNew();
        var activity = Add(EntryKind.Activity, "進行状況", "");
        TranscriptEntry? assistant = null;
        TranscriptEntry? thinking = null;
        Stopwatch? assistantClock = null;
        Stopwatch? thinkingClock = null;
        var loggedThinking = false;
        var loggedResponse = false;
        var aiCallCount = 0;
        var rawStream = new StringBuilder();   // 現在のAI呼び出しの揮発性ライブ出力（進捗プレビュー専用）
        var volatileTail = "";                 // 「進行状況」末尾に付けている揮発プレビュー文字列（未保存）

        AppendActivity(FormatRunConfig());
        AppendActivity("AIに送信しました。応答を待っています。");

        try
        {
            await foreach (var ev in _orchestrator.RunTurnAsync(_conversation, text, _currentSessionId, _cts.Token))
            {
                switch (ev)
                {
                    case ThinkingDelta think:
                        if (!loggedThinking)
                        {
                            AppendActivity("モデルが思考を生成しています。");
                            loggedThinking = true;
                        }
                        if (thinking is null)
                        {
                            thinking = Add(EntryKind.Thinking, "💭 思考", "");
                            thinkingClock = Stopwatch.StartNew();
                        }
                        thinking.AppendText(think.Text);
                        // 進捗状況に「いま何を考えているか」を逐次プレビュー表示する。
                        SetStatus($"💭 思考中… {StreamPreview(thinking.Text)}");
                        break;

                    case RawTextDelta raw:
                        // 揮発性のライブ出力：トランスクリプト・履歴には残さず、「進行状況」エントリの末尾に
                        // 「いま生成中の生テキスト」を逐次プレビューする（確定すると揮発タグごと取り除かれる）。
                        if (!loggedResponse)
                        {
                            AppendActivity("回答本文の生成を開始しました。");
                            SetStatus("応答生成中…");
                            loggedResponse = true;
                        }
                        rawStream.Append(raw.Text);
                        // 末尾だけ流すのではなく、生成済みの全文を改行を保ったまま貯めて見せる（確定時に揮発タグごと消える）。
                        var preview = rawStream.ToString().Trim();
                        SetVolatile(preview.Length == 0 ? "" : $"💬 生成中:{Environment.NewLine}{preview}");
                        break;

                    case TextDelta delta:
                        if (!loggedResponse)
                        {
                            AppendActivity("回答本文の生成を開始しました。");
                            loggedResponse = true;
                        }
                        FinishTimedEntry(ref thinking, ref thinkingClock, "💭 思考");
                        thinking = null; // 本文に入ったので思考ブロックを区切る
                        if (assistant is null)
                        {
                            assistant = Add(EntryKind.Assistant, "🤖 エージェント", "");
                            assistantClock = Stopwatch.StartNew();
                        }
                        assistant.AppendText(delta.Text);
                        SetStatus("応答生成中…");
                        break;

                    case ToolUseRequested req:
                        FinishTimedEntry(ref thinking, ref thinkingClock, "💭 思考");
                        thinking = null;
                        // AIがツール呼び出しと一緒に生成した本文（説明・narration）は、独立した
                        // 「🤖 エージェント」エントリにはせず、ツールカードへ畳んで併記する。
                        // 本文 → 複数ツールの場合は最初のツールにのみ付け、以降はそのまま引数だけ出す。
                        var narration = assistant?.Text;
                        if (assistant is not null)
                        {
                            Transcript.Remove(assistant);   // ライブ表示していた本文エントリを取り下げ、カードへ畳む
                            assistant = null;               // テキストブロックを区切る
                            assistantClock = null;
                        }
                        // 進捗状況に「どのツールを何の引数で呼ぶか」を表示する。
                        SetStatus($"🔧 {req.ToolUse.Name} を準備中… {StreamPreview(req.ToolUse.ArgumentsJson)}");
                        AppendActivity($"{req.ToolUse.Name} の呼び出しを準備しています: {StreamPreview(req.ToolUse.ArgumentsJson)}");
                        Add(EntryKind.Tool, $"🔧 {req.ToolUse.Name}", ComposeToolCard(narration, req.ToolUse.ArgumentsJson, req.ToolUse.RawJson));
                        break;

                    case ApprovalRequested approval:
                        SetStatus($"⏳ {approval.ToolName} の承認待ち…");
                        AppendActivity($"{approval.ToolName} の実行承認を待っています。");
                        break;

                    case ToolExecutionStarted started:
                        // 進捗状況に「いま実行しているコマンド」を表示する。
                        SetStatus($"🔧 {started.ToolUse.Name} を実行中… {StreamPreview(started.ToolUse.ArgumentsJson)}");
                        AppendActivity($"{started.ToolUse.Name} を実行しています: {StreamPreview(started.ToolUse.ArgumentsJson)}");
                        break;

                    case ToolExecutionCompleted done:
                        // ツール結果を踏まえてAIが再応答する。直前ツールの結果概要も進捗状況に出す。
                        SetStatus($"考え中…（直前 {done.ToolUse.Name}: {(done.Result.IsError ? "エラー" : "完了")} {StreamPreview(done.Result.Content)}）");
                        AppendActivity($"{done.ToolUse.Name} が完了しました（{(done.Result.IsError ? "エラー" : "成功")}）: {StreamPreview(done.Result.Content)}。結果を踏まえて次の応答を待っています。");
                        Add(EntryKind.Tool, $"↳ 結果 ({done.ToolUse.Name})", Truncate(done.Result.Content));
                        break;

                    case ToolCallParseFailed parseFailed:
                        // 何が出力されたか分かるよう生出力を見せ、AI に再試行させる旨を示す。
                        FinishTimedEntry(ref thinking, ref thinkingClock, "💭 思考");
                        thinking = null;
                        if (assistant is not null)
                        {
                            Transcript.Remove(assistant);   // ライブ表示していた本文を取り下げてカードへ畳む
                            assistant = null;
                            assistantClock = null;
                        }
                        SetStatus("⚠️ ツール呼び出しJSONが不正。AIに再試行させています…");
                        AppendActivity("ツール呼び出しのJSONが不正でした。モデルの生出力を表示し、正しいJSONで出し直させます。");
                        Add(EntryKind.Error, "⚠️ 不正なツール出力（再試行）", parseFailed.RawText);
                        break;

                    case AgentError err:
                        AppendActivity($"エラーで停止しました。合計 {FormatDuration(turnClock.Elapsed)} かかりました。");
                        Add(EntryKind.Error, "⚠️ エラー", err.Message);
                        break;

                    case AiUsageReported usage:
                        aiCallCount++;
                        AppendActivity(FormatUsage(usage, aiCallCount));
                        rawStream.Clear();   // このAI呼び出しは終了。次の呼び出しの揮発プレビューを新規に始める
                        break;

                    case TurnCompleted:
                        AppendActivity($"回答が完了しました。合計 {FormatDuration(turnClock.Elapsed)} かかりました。");
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            AppendActivity($"ユーザー操作で中断しました。合計 {FormatDuration(turnClock.Elapsed)} かかりました。");
            Add(EntryKind.Error, "⏹ 中断", "ユーザーにより中断されました。");
        }
        catch (Exception ex)
        {
            AppendActivity($"例外で停止しました。合計 {FormatDuration(turnClock.Elapsed)} かかりました。");
            Add(EntryKind.Error, "⚠️ 例外", ex.Message);
        }
        finally
        {
            ClearVolatile();   // 揮発プレビューが残ったまま ProgressLog に保存されないよう必ず片付ける
            turnClock.Stop();
            activity.Header = $"進行状況 ({FormatDuration(turnClock.Elapsed)})";
            FinishTimedEntry(ref assistant, ref assistantClock, "🤖 エージェント");
            FinishTimedEntry(ref thinking, ref thinkingClock, "💭 思考");
            IsBusy = false;
            ClearStatus();
            _cts?.Dispose();
            _cts = null;

            // このターンの進行状況ログを、ターンを開始した user メッセージへ畳んで永続化する
            // （セッション復元時に進捗表示を再構築できるようにする）。
            var turnUser = _conversation.Messages.LastOrDefault(m => m.Role == ChatRole.User);
            if (turnUser is not null) turnUser.ProgressLog = activity.Text;

            // ターン終了時にセッションを自動保存（新規なら採番）
            try { _currentSessionId = _sessions.Save(_currentSessionId, _conversation); }
            catch { /* 保存失敗は会話を妨げない */ }
        }

        void AppendActivity(string message)
        {
            ClearVolatile();   // 揮発プレビューを挟まないよう、恒久ログを足す前に末尾を片付ける
            var prefix = activity.Text.Length == 0 ? "" : Environment.NewLine;
            activity.AppendText($"{prefix}[{FormatDuration(turnClock.Elapsed)}] {message}");
        }

        // 「進行状況」エントリ末尾に付ける揮発プレビュー（保存対象の ProgressLog には残さない）。
        // 末尾に現在の揮発タグぶんだけ後付けし、更新時は古いタグを切り落としてから付け直す。
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

    /// <summary>ツールカードの本文を組み立てる。AIがツール呼び出しと一緒に生成した本文（説明・narration）が
    /// あれば引数JSONの上に併記し、無ければ引数JSONのみを示す。</summary>
    private static string ComposeToolCard(string? narration, string argumentsJson, string? rawJson)
    {
        var text = narration?.Trim();
        var raw = rawJson?.Trim();
        var body = string.IsNullOrEmpty(text)
            ? "arguments:" + Environment.NewLine + argumentsJson
            : text + Environment.NewLine + "arguments:" + Environment.NewLine + argumentsJson;

        if (!string.IsNullOrEmpty(raw) && raw != argumentsJson)
            body += Environment.NewLine + "raw:" + Environment.NewLine + raw;

        return body;
    }

    private static void FinishTimedEntry(ref TranscriptEntry? entry, ref Stopwatch? clock, string baseHeader)
    {
        if (entry is null || clock is null) return;
        clock.Stop();
        entry.Header = $"{baseHeader} ({FormatDuration(clock.Elapsed)})";
        clock = null;
    }

    /// <summary>このターンで使うAIの実行構成（モデル・コンテキスト長・実行EP）を進行状況の1行に整形する。
    /// 何のモデル・設定で動いているのかを毎ターン先頭に出して、遅さ等の原因切り分けに使えるようにする。</summary>
    private string FormatRunConfig()
    {
        var cfg = _settings.Local;
        var model = string.IsNullOrWhiteSpace(cfg.Model) ? "(未設定)" : cfg.Model;
        var numCtx = ModelProfiles.EffectiveNumCtx(cfg.Model, cfg.NumCtx);

        return $"⚙️ 実行構成: モデル {model}｜num_ctx {numCtx}｜EP CPU(ONNX)";
    }

    /// <summary>AI利用統計を進行状況の1行に整形する。トークン数と、重みロード／prefill／decode の
    /// 段階別所要を併記して「どの段階で時間を使ったか」をその場で分かるようにする。</summary>
    private static string FormatUsage(AiUsageReported u, int call)
    {
        var parts = new List<string>();
        if (u.InputTokens is { } it && u.OutputTokens is { } ot)
            parts.Add($"トークン 入力{it}/出力{ot}");
        else if (u.OutputTokens is { } o)
            parts.Add($"出力{o}トークン");

        var stages = new List<string>();
        if (u.LoadMs is { } load && load >= 1) stages.Add($"ロード{FormatMs(load)}");
        if (u.PromptEvalMs is { } pe) stages.Add($"prefill{FormatMs(pe)}");
        if (u.EvalMs is { } ev) stages.Add($"decode{FormatMs(ev)}");
        if (stages.Count > 0)
        {
            var stageText = string.Join("・", stages);
            if (u.TotalMs is { } total) stageText += $"（計{FormatMs(total)}）";
            parts.Add(stageText);
        }

        var body = parts.Count > 0 ? string.Join("｜", parts) : "詳細なし";
        return $"📊 AI内訳#{call}: {body}";
    }

    /// <summary>ストリーミング中の本文／思考の「いま出力している内容」を進捗状況の1行プレビューに整形する。
    /// 改行・連続空白を畳んで末尾の一定文字数だけ見せ、長ければ先頭に省略記号を付ける。</summary>
    private static string StreamPreview(string text, int max = 48)
    {
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        if (flat.Length == 0) return "";
        return flat.Length <= max ? flat : "…" + flat[^max..];
    }

    private static string FormatMs(double ms) => FormatDuration(TimeSpan.FromMilliseconds(ms));

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
