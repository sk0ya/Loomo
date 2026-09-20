using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Observability;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>トレース一覧の1セッション。</summary>
public sealed class TraceSessionItem
{
    public required string SessionId { get; init; }
    public required DateTime UpdatedAt { get; init; }
    public required long SizeBytes { get; init; }
    public string UpdatedLabel => UpdatedAt.ToString("MM-dd HH:mm");
    public string SizeLabel => SizeBytes < 1024 ? $"{SizeBytes} B" : $"{SizeBytes / 1024.0:0.#} KB";
}

/// <summary>タイムラインの1行（トレース1イベント）。</summary>
public sealed class TraceRowVm
{
    public required string Time { get; init; }
    public required string Kind { get; init; }
    public required string Summary { get; init; }
    /// <summary>payload の整形 JSON（行選択時の詳細表示）。</summary>
    public required string Detail { get; init; }
    /// <summary>ターン開始行（タイムラインの区切りとして強調表示）。</summary>
    public bool IsTurnStart { get; init; }
    public bool IsError { get; init; }
}

/// <summary>
/// トレース閲覧セッションペインの ViewModel。<see cref="JsonlTraceSink"/> が書いた
/// traces/*.jsonl を <see cref="TraceReader"/> で読み、ターン区切りのタイムライン
/// （ツール呼び出し・承認・ai.usage の load/prefill/decode 内訳）として表示する。読み取り専用。
/// </summary>
public sealed partial class TraceSessionViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    /// <summary>監視イベント→再読込のデバウンス幅。エージェント実行中の連続追記をまとめる。</summary>
    private const int RefreshDebounceMs = 700;

    private readonly TraceReader _reader;
    private bool _loaded;
    private FileSystemWatcher? _watcher;
    private Timer? _refreshDebounce;

    [ObservableProperty] private TraceSessionItem? _selectedSession;
    [ObservableProperty] private TraceRowVm? _selectedRow;
    [ObservableProperty] private string _detailText = "";
    /// <summary>選択セッションの集計（ターン数・所要・トークン）。</summary>
    [ObservableProperty] private string _summaryLabel = "";
    [ObservableProperty] private string _emptyMessage = "";

    public ObservableCollection<TraceSessionItem> Sessions { get; } = new();
    public ObservableCollection<TraceRowVm> Rows { get; } = new();

    public TraceSessionViewModel(TraceReader reader) => _reader = reader;

    /// <summary>トレースペインが初めて表示されたときに一覧を読み込み、以降はフォルダ監視で自動更新する。</summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        StartWatching();
        _ = RefreshAsync();
    }

    /// <summary>traces フォルダを監視し、JsonlTraceSink の追記・ローテーションに追従する。</summary>
    private void StartWatching()
    {
        try
        {
            Directory.CreateDirectory(_reader.DirectoryPath);
            var watcher = new FileSystemWatcher(_reader.DirectoryPath, "*.jsonl")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += (_, _) => ScheduleRefresh();
            watcher.Created += (_, _) => ScheduleRefresh();
            watcher.Deleted += (_, _) => ScheduleRefresh();
            watcher.Renamed += (_, _) => ScheduleRefresh();
            watcher.Error += (_, _) => ScheduleRefresh();
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception)
        {
            // 監視を張れなくても閲覧自体はできる（手動でペインを開き直せば再読込される）
        }
    }

    private void ScheduleRefresh()
    {
        _refreshDebounce ??= new Timer(_ =>
        {
            var app = Application.Current;
            if (app is null) return;
            app.Dispatcher.BeginInvoke(new Action(() => _ = RefreshAsync()));
        });
        _refreshDebounce.Change(RefreshDebounceMs, Timeout.Infinite);
    }

    private async Task RefreshAsync()
    {
        _loaded = true;
        var selectedId = SelectedSession?.SessionId;
        var files = await Task.Run(() => _reader.List());

        Sessions.Clear();
        TraceSessionItem? reselect = null;
        foreach (var f in files)
        {
            var item = new TraceSessionItem
            {
                SessionId = f.SessionId,
                UpdatedAt = f.UpdatedAt,
                SizeBytes = f.SizeBytes,
            };
            Sessions.Add(item);
            if (f.SessionId == selectedId)
                reselect = item;
        }

        EmptyMessage = Sessions.Count > 0 ? "" : "トレースがありません（設定の「AI操作トレースを記録」を確認してください）。";
        SelectedSession = reselect ?? Sessions.FirstOrDefault();
        if (SelectedSession is null)
        {
            Rows.Clear();
            SummaryLabel = "";
            DetailText = "";
        }
    }

    /// <summary>選択セッションのトレースを削除する。</summary>
    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedSession is not { } session) return;
        var answer = MessageBox.Show(
            Application.Current?.MainWindow!,
            $"トレース {session.SessionId} を削除しますか？",
            "トレースの削除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        await Task.Run(() => _reader.Delete(session.SessionId));
        await RefreshAsync();
    }

    partial void OnSelectedSessionChanged(TraceSessionItem? value) => _ = LoadRowsAsync(value);

    partial void OnSelectedRowChanged(TraceRowVm? value) => DetailText = value?.Detail ?? "";

    private async Task LoadRowsAsync(TraceSessionItem? session)
    {
        // jsonl は追記のみなので、自動更新の再読込では同じインデックスの行を選び直せる
        var keepIndex = SelectedRow is { } sel ? Rows.IndexOf(sel) : -1;
        Rows.Clear();
        SummaryLabel = "";
        DetailText = "";
        if (session is null) return;

        var events = await Task.Run(() => _reader.Read(session.SessionId));

        long totalIn = 0, totalOut = 0, totalTurnMs = 0;
        var turns = 0;
        foreach (var ev in events)
        {
            var payload = ev.Payload is JsonElement el ? el : (JsonElement?)null;
            if (ev.Kind == TraceKinds.TurnCompleted)
            {
                turns++;
                totalTurnMs += GetLong(payload, "durationMs") ?? 0;
            }
            if (ev.Kind == TraceKinds.AiUsage)
            {
                totalIn += GetLong(payload, "inputTokens") ?? 0;
                totalOut += GetLong(payload, "outputTokens") ?? 0;
            }
            Rows.Add(ToRow(ev, payload));
        }

        SummaryLabel = events.Count == 0 ? "（イベントなし）"
            : $"ターン {turns}・合計 {totalTurnMs / 1000.0:0.#}s・in {totalIn:N0} tok / out {totalOut:N0} tok";

        if (keepIndex >= 0 && keepIndex < Rows.Count)
            SelectedRow = Rows[keepIndex];
    }

    // ===== イベント → 表示行 =====

    private static TraceRowVm ToRow(TraceEvent ev, JsonElement? p)
    {
        var (kindLabel, summary, isError) = Describe(ev.Kind, p);
        return new TraceRowVm
        {
            Time = ev.Ts.ToLocalTime().ToString("HH:mm:ss.f"),
            Kind = kindLabel,
            Summary = summary,
            Detail = p is { } el ? JsonSerializer.Serialize(el, PrettyJson) : "",
            IsTurnStart = ev.Kind == TraceKinds.TurnStarted,
            IsError = isError,
        };
    }

    private static (string Kind, string Summary, bool IsError) Describe(string kind, JsonElement? p)
        => kind switch
        {
            TraceKinds.SessionStarted => ("セッション開始", $"provider={GetStr(p, "provider")}", false),
            TraceKinds.TurnStarted => ("ターン開始", Truncate(GetStr(p, "userInput")), false),
            TraceKinds.AiMessage when GetBool(p, "started") == true
                => ("AI呼び出し", $"反復 {GetLong(p, "iteration")}（ツール {GetLong(p, "toolCount")} 個）", false),
            TraceKinds.AiMessage => ("AI応答", Truncate(GetStr(p, "fullText")), false),
            TraceKinds.AiToolUse => ("ツール要求", $"{GetStr(p, "name")} {Truncate(GetStr(p, "argsJson"))}", false),
            TraceKinds.SafetyEvaluated when GetBool(p, "blocked") == true
                => ("安全評価", $"{GetStr(p, "tool")} をブロック: {Truncate(GetStr(p, "reason"))}", true),
            TraceKinds.SafetyEvaluated => ("安全評価", $"{GetStr(p, "tool")} 許可", false),
            TraceKinds.ApprovalRequested => ("承認要求", Truncate(GetStr(p, "summary")), false),
            TraceKinds.ApprovalResolved => ("承認結果",
                $"{(GetBool(p, "approved") == true ? "承認" : "拒否")}（待ち {GetLong(p, "waitMs")}ms）",
                GetBool(p, "approved") != true),
            TraceKinds.ToolStarted => ("ツール実行", GetStr(p, "name"), false),
            TraceKinds.ToolCompleted => ("ツール完了",
                $"{GetStr(p, "name")}・{GetLong(p, "durationMs")}ms"
                + (GetBool(p, "isError") == true ? $"・エラー {Truncate(GetStr(p, "error"))}" : ""),
                GetBool(p, "isError") == true),
            TraceKinds.AiUsage => ("AI内訳",
                $"in {GetLong(p, "inputTokens"):N0} / out {GetLong(p, "outputTokens"):N0} tok・"
                + $"load {GetLong(p, "loadMs")}ms・prefill {GetLong(p, "promptEvalMs")}ms・decode {GetLong(p, "evalMs")}ms",
                false),
            TraceKinds.TurnCompleted => ("ターン完了",
                $"反復 {GetLong(p, "iterations")} 回・{(GetLong(p, "durationMs") ?? 0) / 1000.0:0.#}s", false),
            TraceKinds.Error => ("エラー", $"{Truncate(GetStr(p, "message"))}（{GetStr(p, "where")}）", true),
            _ => (kind, "", false),
        };

    // ===== JsonElement ヘルパ =====

    private static string GetStr(JsonElement? p, string name)
        => p is { } el && el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static long? GetLong(JsonElement? p, string name)
        => p is { } el && el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number
            ? (v.TryGetInt64(out var l) ? l : (long)v.GetDouble())
            : null;

    private static bool? GetBool(JsonElement? p, string name)
        => p is { } el && el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    private static string Truncate(string text)
    {
        var t = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return t.Length <= 120 ? t : t[..120] + "…";
    }
}
