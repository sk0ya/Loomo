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
        foreach (var stage in _warmup.StageTimings.Where(stage => stage.Elapsed >= TimeSpan.FromMilliseconds(1)))
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
                    // 保存テキストしか無いので、段階タイムラインを起こし直して表示する。
                    if (!string.IsNullOrEmpty(m.ProgressLog))
                        Add(EntryKind.Activity, "進行状況", m.ProgressLog).HydrateActivitySteps();
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
    private void ShowChatMode()
    {
        Mode = AiBarMode.Chat;
        IsExpanded = true;

        // ワークフロー表示中にウォームアップが完了した場合、完了カードはチャット面に隠れている。
        // 戻ってきた時点で処理中でなければ、直近の内訳を再描画して「何が終わったか」を見失わない。
        if (!IsBusy && !_warmup.IsWarmingUp && _warmup.TotalDuration is not null)
            RenderWarmupCompletion();
    }

    /// <summary>ワークフローモードに切替える（セグメントボタン）。保存済み一覧を最新化する。</summary>
    [RelayCommand]
    private void ShowWorkflowMode()
    {
        Mode = AiBarMode.Workflow;
        IsExpanded = true;
        Workflow.RefreshSavedWorkflowsAndSelectFirstIfNeeded();
    }

    partial void OnModeChanged(AiBarMode value)
    {
        OnPropertyChanged(nameof(IsChatMode));
        OnPropertyChanged(nameof(IsWorkflowMode));
    }

    private static string DisplayWarmupStageName(string status)
    {
        var detail = "";
        var match = Regex.Match(status, @"（(?<detail>.*?)）");
        if (match.Success)
            detail = match.Groups["detail"].Value.Trim();

        var baseName = Regex.Replace(status, @"（.*?）", "").Trim();
        var label = baseName switch
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
            _ => baseName
        };

        return string.IsNullOrWhiteSpace(detail) ? label : $"{label}（{detail}）";
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
