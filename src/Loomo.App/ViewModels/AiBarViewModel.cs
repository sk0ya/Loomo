using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Ai.Clients;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>AIバーの表示モード。チャット（エージェントループ）とワークフロー（単発指示の連鎖）を切替える。</summary>
public enum AiBarMode { Chat, Workflow }

/// <summary>入力欄で「/」から呼び出すスラッシュコマンド1件（補完候補に出す）。</summary>
public sealed record ChatCommand(string Name, string Description);

public sealed record WarmupCompletionStage(string Name, string Duration);

/// <summary>下部AIバー（全幅・展開式）の ViewModel。エージェントループを駆動する。</summary>
public sealed partial class AiBarViewModel : ObservableObject
{
    private readonly AgentOrchestrator _orchestrator;
    private readonly AiSettings _settings;
    private readonly SettingsViewModel _settingsVm;
    private readonly ConversationStore _sessions;
    private readonly IAiWarmup _warmup;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private Conversation _conversation = new();
    private string? _currentSessionId;
    private string? _lastClosedSessionId;   // /resume で復元する直前に閉じたセッション
    private bool _suppressSuggestions;       // プログラムからの Input 書換時に補完を抑止
    private bool _suppressWarmupCompletion;   // 送信に伴うウォームアップは完了内訳を出さない（直後にターンが始まるため）
    private bool _wasWarmingUp;                // 直前に「ウォームアップ中」だったか（中→完了の遷移を一度だけ拾う）
    private CancellationTokenSource? _cts;

    // ↑/↓ の入力履歴ナビゲーション（実体は PromptInputHistory）。
    private readonly PromptInputHistory _inputHistory;

    public ObservableCollection<TranscriptEntry> Transcript { get; } = new();

    /// <summary>利用可能なスラッシュコマンド一覧。</summary>
    public static IReadOnlyList<ChatCommand> AllCommands { get; } = new[]
    {
        new ChatCommand("/model", "モデルを切替（/model で一覧・/model 名前 で選択）"),
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

    /// <summary>現在の表示モード（チャット／ワークフロー）。切替はビューのセグメントボタンから。</summary>
    [ObservableProperty] private AiBarMode _mode = AiBarMode.Chat;

    public bool IsChatMode => Mode == AiBarMode.Chat;
    public bool IsWorkflowMode => Mode == AiBarMode.Workflow;

    /// <summary>ワークフローモードの ViewModel（同じAIペイン内でチャットと切替えて表示する）。</summary>
    public WorkflowViewModel Workflow { get; }

    /// <summary>AIウォームアップの実行中か。実行中は AI への指示（送信）を受け付けず、
    /// その旨をバーに表示する。</summary>
    [ObservableProperty] private bool _isWarmingUp;

    [ObservableProperty] private string _warmupStatusText = "";

    [ObservableProperty] private bool _isWarmupCompletionVisible;

    [ObservableProperty] private string _warmupCompletionTotalText = "";

    public ObservableCollection<WarmupCompletionStage> WarmupCompletionStages { get; } = new();

    [ObservableProperty] private string _providerLabel;

    /// <summary>処理中に「いま何をしているか」を示すステータス文言（考え中／ツール実行中／承認待ち…）。
    /// 経過秒を併記して、止まっているのではなく動作中だと分かるようにする。</summary>
    [ObservableProperty] private string _statusText = "";

    private string _statusPhase = "";                 // 経過秒を除いたフェーズ説明
    private readonly Stopwatch _statusClock = new();   // 処理開始からの経過時間
    private readonly DispatcherTimer _statusTimer;     // 経過秒の表示を更新する
    private readonly DispatcherTimer _warmupTimer;     // ウォームアップ経過秒の表示を更新する

    public AiBarViewModel(
        AgentOrchestrator orchestrator,
        UiApprovalService approval,
        AiSettings settings,
        SettingsViewModel settingsVm,
        ConversationStore sessions,
        PromptHistoryStore historyStore,
        IAiWarmup warmup,
        WorkflowViewModel workflow)
    {
        _orchestrator = orchestrator;
        _settings = settings;
        _settingsVm = settingsVm;
        _sessions = sessions;
        _warmup = warmup;
        Workflow = workflow;
        _inputHistory = new PromptInputHistory(historyStore);   // 前回までの送信履歴を引き継ぐ
        _providerLabel = settings.Provider.ToString();
        approval.ApprovalRequested += OnApprovalRequested;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) => RenderStatus();

        _warmupTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _warmupTimer.Tick += (_, _) => RenderWarmupStatus();

        // ウォームアップの実行中は送信を抑止し、バーに状態と経過時間を出す。
        _isWarmingUp = warmup.IsWarmingUp;
        warmup.StateChanged += OnWarmupStateChanged;
        ApplyWarmupState();
    }

    /// <summary>暖機サービスからの状態変化通知。UIスレッドへ整えてから反映する。</summary>
    private void OnWarmupStateChanged()
    {
        if (_dispatcher.CheckAccess()) ApplyWarmupState();
        else _dispatcher.BeginInvoke(ApplyWarmupState);
    }

    private void ApplyWarmupState()
    {
        IsWarmingUp = _warmup.IsWarmingUp;
        if (IsWarmingUp)
        {
            _wasWarmingUp = true;
            IsWarmupCompletionVisible = false;
            WarmupCompletionTotalText = "";
            WarmupCompletionStages.Clear();
            if (!_warmupTimer.IsEnabled) _warmupTimer.Start();
            RenderWarmupStatus();
        }
        else
        {
            _warmupTimer.Stop();
            WarmupStatusText = "";

            // ウォームアップ「中→完了」の遷移のときだけ完了内訳を扱う。SetStatus（段階表示）は
            // エンジンの Task.Run 内＝バックグラウンドスレッドからも飛ぶため、その StateChanged が
            // BeginInvoke で遅れて届くと、完了後（IsWarmingUp は既に false）にこの else が再実行される。
            // 遷移を一度だけ拾うことで、その遅延コールバックが完了内訳を再描画して「残り続ける」のを防ぐ。
            if (!_wasWarmingUp)
                return;
            _wasWarmingUp = false;

            if (_suppressWarmupCompletion)
            {
                // 送信に伴って実行したウォームアップは、直後にこのターンが始まるので完了内訳は出さない。
                IsWarmupCompletionVisible = false;
                WarmupCompletionTotalText = "";
                WarmupCompletionStages.Clear();
            }
            else
            {
                RenderWarmupCompletion();
            }
        }
    }

    private void RenderWarmupStatus()
    {
        if (_warmup.WarmupStartedAt is not { } startedAt)
        {
            WarmupStatusText = "";
            return;
        }

        var elapsed = DateTimeOffset.Now - startedAt;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var current = string.IsNullOrWhiteSpace(_warmup.CurrentStatus)
            ? "モデルとプロンプトを準備しています"
            : _warmup.CurrentStatus;
        WarmupStatusText = $"ウォームアップ中… {current}。{FormatWarmupDuration(elapsed)} 経過（完了までAIへの指示はできません）";
    }

    private void RenderWarmupCompletion()
    {
        WarmupCompletionStages.Clear();
        foreach (var stage in _warmup.StageTimings.Where(ShouldShowWarmupStage))
            WarmupCompletionStages.Add(new WarmupCompletionStage(DisplayWarmupStageName(stage.Name), FormatDuration(stage.Elapsed)));

        WarmupCompletionTotalText = _warmup.TotalDuration is { } total
            ? FormatDuration(total)
            : "";
        IsWarmupCompletionVisible = WarmupCompletionStages.Count > 0 || !string.IsNullOrWhiteSpace(WarmupCompletionTotalText);
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

    /// <summary>FolderTree の「AI-誤字脱字チェック」から呼ばれる。現在のセッションを /clear で閉じて
    /// 新規開始し、指定ファイルの誤字脱字チェックを依頼するプロンプトを送信する。処理中・暖機中は何もしない。</summary>
    public void RunTypoCheck(string filePath)
    {
        if (IsBusy || IsWarmingUp)
            return;

        ClearSession();
        SetInput($"次のファイルの誤字脱字・変換ミス・タイプミスをチェックし、問題箇所を該当行とともに一覧で報告してください。" +
                 $"修正は加えず指摘のみで構いません。対象ファイル: {filePath}");
        if (SendCommand.CanExecute(null))
            SendCommand.Execute(null);
    }

    /// <summary>ターミナル/エディタの「AIに聞く」から呼ばれる。選択テキストについて尋ねるプロンプトを
    /// 現在のセッションへ即送信する（誤字脱字チェックと違いセッションは閉じない）。処理中・暖機中・
    /// 空テキストのときは何もしない。</summary>
    public void AskAbout(string selectedText)
    {
        if (IsBusy || IsWarmingUp || string.IsNullOrWhiteSpace(selectedText))
            return;

        IsExpanded = true;
        SetInput($"次の内容について教えてください。\n\n{selectedText}");
        if (SendCommand.CanExecute(null))
            SendCommand.Execute(null);
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
                            Add(EntryKind.Tool, ToolUseHeader(use.Name, use.ArgumentsJson), text);
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(m.Text))
                    {
                        Add(EntryKind.Assistant, "エージェント", m.Text);
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

    /// <summary>チャットモードに切替える（セグメントボタン）。</summary>
    [RelayCommand]
    private void ShowChatMode() => Mode = AiBarMode.Chat;

    /// <summary>ワークフローモードに切替える（セグメントボタン）。保存済み一覧を最新化する。</summary>
    [RelayCommand]
    private void ShowWorkflowMode()
    {
        Mode = AiBarMode.Workflow;
        IsExpanded = true;
        Workflow.RefreshSavedWorkflows();
    }

    partial void OnModeChanged(AiBarMode value)
    {
        OnPropertyChanged(nameof(IsChatMode));
        OnPropertyChanged(nameof(IsWorkflowMode));
    }

    private bool CanSend() => !IsBusy && !IsWarmingUp && !string.IsNullOrWhiteSpace(Input);

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
        _cts = new CancellationTokenSource();

        // モデル未ロードならチャット実行前にウォームアップしておく。これをしないと最初のターンで
        // 数十秒のモデルロードが「考え中…」のまま無反応に見える（何が起きているか分からない）。
        // ウォームアップ中は IsWarmingUp 表示が出る（送信は CanSend が抑止）。
        // このウォームアップは送信に伴うもので、直後にこのターンが始まるため「完了」内訳表示は出さない。
        _suppressWarmupCompletion = true;
        try { await _warmup.EnsureWarmAsync(_cts.Token); }
        catch (OperationCanceledException) { /* 後段 RunTurnAsync 側でまとめて扱う */ }

        // 直前（起動時など）のウォームアップ完了表示が残っていれば、このターンの「考え中…」と重なるので畳む。
        IsWarmupCompletionVisible = false;
        WarmupCompletionTotalText = "";
        WarmupCompletionStages.Clear();
        IsBusy = true;
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

        AppendActivity(TranscriptFormatting.FormatRunConfig(_settings));
        AppendActivity("AIに送信しました。応答を待っています。");

        try
        {
            await foreach (var ev in _orchestrator.RunTurnAsync(_conversation, text, _currentSessionId, _cts.Token,
                               turnPreamble: AiSettings.ChatTurnPreamble))
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
                        SetStatus($"💭 思考中… {TranscriptFormatting.StreamPreview(thinking.Text)}");
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
                            assistant = Add(EntryKind.Assistant, "エージェント", "");
                            assistantClock = Stopwatch.StartNew();
                        }
                        assistant.AppendText(delta.Text);
                        SetStatus("応答生成中…");
                        break;

                    case ToolUseRequested req:
                        FinishTimedEntry(ref thinking, ref thinkingClock, "💭 思考");
                        thinking = null;
                        // ツールが確定したので、配列の生 JSON を見せていた揮発プレビューは消す
                        // （以降はツールカードで表示する。複数ツールでも二重表示にならない）。
                        rawStream.Clear();
                        SetVolatile("");
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
                        SetStatus($"🔧 {req.ToolUse.Name} を準備中… {TranscriptFormatting.StreamPreview(req.ToolUse.ArgumentsJson)}");
                        AppendActivity($"{req.ToolUse.Name} の呼び出しを準備しています: {TranscriptFormatting.StreamPreview(req.ToolUse.ArgumentsJson)}");
                        Add(EntryKind.Tool, ToolUseHeader(req.ToolUse.Name, req.ToolUse.ArgumentsJson), ComposeToolCard(narration, req.ToolUse.ArgumentsJson, req.ToolUse.RawJson));
                        break;

                    case ApprovalRequested approval:
                        SetStatus($"⏳ {approval.ToolName} の承認待ち…");
                        AppendActivity($"{approval.ToolName} の実行承認を待っています。");
                        break;

                    case ToolExecutionStarted started:
                        // 進捗状況に「いま実行しているコマンド」を表示する。
                        SetStatus($"🔧 {started.ToolUse.Name} を実行中… {TranscriptFormatting.StreamPreview(started.ToolUse.ArgumentsJson)}");
                        AppendActivity($"{started.ToolUse.Name} を実行しています: {TranscriptFormatting.StreamPreview(started.ToolUse.ArgumentsJson)}");
                        break;

                    case ToolExecutionCompleted done:
                        // ツール結果を踏まえてAIが再応答する。直前ツールの結果概要も進捗状況に出す。
                        SetStatus($"考え中…（直前 {done.ToolUse.Name}: {(done.Result.IsError ? "エラー" : "完了")} {TranscriptFormatting.StreamPreview(done.Result.Content)}）");
                        AppendActivity($"{done.ToolUse.Name} が完了しました（{(done.Result.IsError ? "エラー" : "成功")}）: {TranscriptFormatting.StreamPreview(done.Result.Content)}。結果を踏まえて次の応答を待っています。");
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
                        AppendActivity(TranscriptFormatting.FormatUsage(usage, aiCallCount));
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
            // モデルがロード済みでウォームアップが走らなかった場合などに抑止フラグが次の自発的
            // ウォームアップへ漏れないよう、ターン終了時に必ず戻す（終了遷移で既に消費されていれば no-op）。
            _suppressWarmupCompletion = false;
            ClearVolatile();   // 揮発プレビューが残ったまま ProgressLog に保存されないよう必ず片付ける
            turnClock.Stop();
            activity.Header = $"進行状況 ({FormatDuration(turnClock.Elapsed)})";
            FinishTimedEntry(ref assistant, ref assistantClock, "エージェント");
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
        // チャットのターン実行中だけ処理する。ワークフロー実行中の承認は WorkflowViewModel が
        // 自分のステップログへ橋渡しするため、ここでは拾わない（同じ singleton イベントの二重処理を防ぐ）。
        if (!IsBusy) return;

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
        // ツール使用・ツール結果は既定で折りたたむ（ヘッダーの1行要約だけ常時見え、詳細は開いたときだけ）。
        var entry = new TranscriptEntry { Kind = kind, Header = header, Text = text, IsCollapsed = kind == EntryKind.Tool };
        Transcript.Add(entry);
        return entry;
    }

    private static string Truncate(string s, int max = 2000)
        => s.Length <= max ? s : s[..max] + $"\n…(+{s.Length - max} 文字)";

    /// <summary>ツール使用エントリのヘッダー（折りたたんでも常時見える1行）を組み立てる。
    /// 「🔧 ツール名: 主要引数」の形で、何をしたかが一目で分かるようにする
    /// （run_powershell=コマンド／write_file・edit_file=パス／web_search=検索語）。</summary>
    private static string ToolUseHeader(string toolName, string argumentsJson)
    {
        var summary = SummarizeToolArgs(toolName, argumentsJson);
        return string.IsNullOrEmpty(summary) ? $"🔧 {toolName}" : $"🔧 {toolName}: {summary}";
    }

    /// <summary>ツールの引数JSONから代表引数を1つ抜き出し、1行要約に整える。小モデルが別名キーで
    /// 送ってきても拾えるよう各 *Contract の別名配列を順に見る。見つからなければ最初の文字列引数で代用。</summary>
    private static string SummarizeToolArgs(string toolName, string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return "";
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "";

            var keys = toolName switch
            {
                PwshContract.ToolName => PwshContract.CommandKeys,
                WriteFileContract.ToolName => WriteFileContract.PathKeys,
                EditFileContract.ToolName => EditFileContract.PathKeys,
                WebSearchContract.ToolName => WebSearchContract.QueryKeys,
                _ => null
            };

            if (keys is not null)
                foreach (var k in keys)
                    if (root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                        return OneLine(v.GetString() ?? "");

            // 未知のツール・代表引数が無い場合は最初の文字列引数で代用する。
            foreach (var p in root.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String)
                    return OneLine(p.Value.GetString() ?? "");
        }
        catch { /* パース不能なら要約なし（ヘッダーはツール名のみになる） */ }
        return "";
    }

    /// <summary>改行・連続空白を畳んで1行にし、長ければ先頭から切って末尾に省略記号を付ける（ヘッダー用）。</summary>
    private static string OneLine(string text, int max = 80)
    {
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

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

    private static string DisplayWarmupStageName(string status)
    {
        var s = Regex.Replace(status, @"（.*?）", "").Trim();
        return s switch
        {
            "ウォームアップを開始しています" => "開始",
            "プロンプトを組み立てています" => "プロンプト",
            "モデル設定を確認しています" => "設定確認",
            "モデルをロードしています" => "モデルロード",
            "プロンプトをトークン化しています" => "トークン化",
            "プロンプトをトークン化しています（モデルはロード済み）" => "トークン化",
            "生成器を準備しています" => "生成器",
            "KVキャッシュを作成しています" => "KVキャッシュ",
            "ウォームアップを完了しています" => "完了処理",
            _ => s
        };
    }

    private static bool ShouldShowWarmupStage(WarmupStageTiming stage)
    {
        if (stage.Elapsed < TimeSpan.FromMilliseconds(1))
            return false;

        var s = Regex.Replace(stage.Name, @"（.*?）", "").Trim();
        return s is not "ウォームアップを開始しています"
            and not "プロンプトを組み立てています"
            and not "モデル設定を確認しています"
            and not "プロンプトをトークン化しています";
    }

    private static string FormatWarmupDuration(TimeSpan elapsed)
    {
        if (elapsed.TotalMinutes < 1)
            return $"{Math.Floor(elapsed.TotalSeconds):0} 秒";
        return $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒";
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
    private void PushHistory(string text) => _inputHistory.Push(text);

    /// <summary>↑：ひとつ前の入力履歴を呼び出す。履歴があれば true（キーを消費）。</summary>
    public bool RecallPreviousHistory()
    {
        if (!_inputHistory.RecallPrevious(Input, out var recalled)) return false;
        if (recalled is not null) SetInput(recalled);
        return true;
    }

    /// <summary>↓：ひとつ後の入力履歴（末尾を超えたら下書きへ戻す）。ナビ中なら true。</summary>
    public bool RecallNextHistory()
    {
        if (!_inputHistory.RecallNext(out var recalled)) return false;
        SetInput(recalled!);
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
            case "/model": RunModelCommand(text); return true;
            case "/clear": ClearSession(); return true;
            case "/resume": ResumeLastSession(); return true;
            default: return false; // 既知コマンドでなければ通常メッセージとして送る
        }
    }

    /// <summary>/model コマンド。引数なしで一覧（現在のモデルに●）、引数ありで切替える。
    /// モデル状態は <see cref="SettingsViewModel"/> を単一の真実として共有し、切替は次のターンから効く。</summary>
    private void RunModelCommand(string text)
    {
        IsExpanded = true;
        _settingsVm.EnsureModelsLoaded();   // ローカルのモデル一覧を最新化（同期的に埋まる）
        var models = _settingsVm.AvailableModels.ToList();
        var current = _settingsVm.Model;
        var arg = text.Length > "/model".Length ? text["/model".Length..].Trim() : "";

        if (arg.Length == 0)
        {
            if (models.Count == 0)
            {
                Add(EntryKind.Info, "🧩 モデル",
                    "ローカルにモデルがありません。設定（⚙）でダウンロード／追加してください。");
                return;
            }
            // クリックで切替できる一覧を出す（● が現在のモデル）。/model <名前> でも切替可能。
            var entry = Add(EntryKind.Info, "🧩 モデル", "クリックで切替できます（● が現在のモデル）。");
            entry.SetModelChoices(models, current, SelectModelByName);
            return;
        }

        // 完全一致を優先し、無ければ部分一致（先頭から1件）で拾う。
        var match = models.FirstOrDefault(m => string.Equals(m, arg, StringComparison.OrdinalIgnoreCase))
            ?? models.FirstOrDefault(m => m.Contains(arg, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            Add(EntryKind.Error, "モデルが見つかりません",
                models.Count == 0
                    ? $"'{arg}' に一致するモデルがありません（ローカルにモデルがありません）。"
                    : $"'{arg}' に一致するモデルがありません。候補: {string.Join(", ", models)}");
            return;
        }

        SelectModelByName(match);
    }

    /// <summary>モデルを切替える（一覧クリック・/model 名前 で共用）。状態は SettingsViewModel が持ち、
    /// パス解決と settings.json 永続化を行う（OnModelChanged）。適用は次のターンから。</summary>
    private void SelectModelByName(string name)
    {
        _settingsVm.Model = name;
        Add(EntryKind.Info, "🧩 モデルを切替えました", $"{name}（次のターンから適用されます）");
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
            _inputHistory.ResetNavigation();   // 手入力したら履歴ナビをリセット
            UpdateCommandSuggestions(value);
        }
    }

    partial void OnIsBusyChanged(bool value) => SendCommand.NotifyCanExecuteChanged();

    partial void OnIsWarmingUpChanged(bool value) => SendCommand.NotifyCanExecuteChanged();
}
