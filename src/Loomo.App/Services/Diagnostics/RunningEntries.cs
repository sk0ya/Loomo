using sk0ya.Loomo.Services.Lsp;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Services;

/// <summary>動いているもの面（§24.54）の 1 行の種類。</summary>
public enum RunningEntryKind { Terminal, Claude, Debug, IdeTask, LanguageServer, Ai }

/// <summary>行の色合い。<see cref="Attention"/> は人の番（承認待ち・ブレークポイントで停止中）。</summary>
public enum RunningEntryTone { Normal, Attention, Failed, Quiet }

/// <summary>動いているもの 1 つ。<see cref="Key"/> は種類＋出所の ID で、引き直しても同じものは同じ鍵になる
/// （行のインスタンスを使い回して、選択やスクロールを壊さない）。</summary>
public sealed record RunningEntry(
    string Key,
    RunningEntryKind Kind,
    string Title,
    string Detail,
    DateTime? SinceUtc,
    RunningEntryTone Tone,
    bool CanStop,
    string Group,
    string? ToolTip = null);

/// <summary>ターミナルタブ 1 つの材料。<paramref name="Agent"/> は Claude Code が居るタブだけ。
/// <paramref name="Programs"/> は Claude 以外のプログラムが OSC 7501 で申告している、いま動いているもの（§24.61）。</summary>
internal sealed record RunningTerminalSource(
    Guid TabId, string TabTitle, TerminalTabStatus Status, ClaudeAgentState? Agent,
    IReadOnlyList<Terminal.Tabs.ProgramStatusRecord>? Programs = null);

/// <summary>デバッグ・実行のセッション 1 つの材料（IDE／TS IDE）。</summary>
internal sealed record RunningDebugSource(string Pane, Guid SessionId, string Name, bool IsBusy, bool IsStopped, string Status);

/// <summary>IDE のビルド・テストなど、セッションではない作業の材料。</summary>
internal sealed record RunningIdeTaskSource(string Pane, string Status);

/// <summary>
/// 部屋の中で動いているものを 1 列に並べる（純ロジック・テスト対象・§24.54）。
/// <para>並びは「人の番（承認待ち・停止中）→ 実行中 → 常駐（言語サーバー）」。袖のバッジ（§24.1）は「どのペインで」、
/// 留守中のまとめ（§24.22）は「終わったこと」——ここは「いま部屋全体で何が走っているか」だけを持つ。</para>
/// </summary>
internal static class RunningEntries
{
    public const string AttentionGroup = "あなたの番";
    public const string RunningGroup = "実行中";
    public const string ResidentGroup = "常駐";
    /// <summary>人が「あとで」に回した行（§24.71）。区切りの下・一番最後に薄く並ぶ。</summary>
    public const string SnoozedGroup = "あとで";

    public static IReadOnlyList<RunningEntry> Build(
        IEnumerable<RunningTerminalSource> terminals,
        IEnumerable<RunningDebugSource> debugSessions,
        IEnumerable<RunningIdeTaskSource> ideTasks,
        IEnumerable<LspServerRuntimeStatus> languageServers,
        bool aiBusy,
        string? aiStatus)
    {
        var entries = new List<RunningEntry>();

        foreach (var t in terminals)
        {
            // プログラムが自分で言う状態（OSC 7501・§24.61）：人を待っているものは「あなたの番」、作業中は「実行中」。
            foreach (var program in t.Programs ?? [])
            {
                var waiting = program.State == Terminal.Tabs.ProgramStatusState.Blocked;
                entries.Add(new RunningEntry(
                    $"program:{t.TabId}:{program.Id}", RunningEntryKind.Terminal,
                    ProgramStatusPresentation.Name(program),
                    $"{ProgramStatusPresentation.Detail(program)} · {t.TabTitle}",
                    t.Status.RunningSinceUtc,
                    waiting ? RunningEntryTone.Attention : RunningEntryTone.Normal,
                    CanStop: false, waiting ? AttentionGroup : RunningGroup,
                    program.Message));
            }
            if (t.Agent is { } agent)
            {
                var attention = agent is ClaudeAgentState.NeedsApproval or ClaudeAgentState.TurnEnded;
                entries.Add(new RunningEntry(
                    $"claude:{t.TabId}", RunningEntryKind.Claude,
                    $"Claude Code — {t.TabTitle}", ClaudeAgentTab.Label(agent),
                    t.Status.RunningSinceUtc,
                    attention ? RunningEntryTone.Attention : RunningEntryTone.Normal,
                    CanStop: agent == ClaudeAgentState.Working,
                    attention ? AttentionGroup : RunningGroup,
                    TerminalTabSummary.Detail(t.Status, DateTime.UtcNow)));
                continue;
            }
            if (!t.Status.IsRunning)
                continue;
            entries.Add(new RunningEntry(
                $"terminal:{t.TabId}", RunningEntryKind.Terminal,
                t.Status.RunningCommand is { } command ? TerminalTabSummary.Shorten(command) : "（コマンド）",
                TerminalTabSummary.Place(t.Status, null) is { Length: > 0 } place ? $"{t.TabTitle} · {place}" : t.TabTitle,
                t.Status.RunningSinceUtc, RunningEntryTone.Normal, CanStop: true, RunningGroup,
                t.Status.RunningCommand));
        }

        foreach (var d in debugSessions)
        {
            if (!d.IsBusy && !d.IsStopped)
                continue;
            entries.Add(new RunningEntry(
                $"debug:{d.SessionId}", RunningEntryKind.Debug,
                d.Name, d.IsStopped ? $"{d.Pane} · 停止中（ブレークポイント）" : $"{d.Pane} · {d.Status}",
                null, d.IsStopped ? RunningEntryTone.Attention : RunningEntryTone.Normal,
                CanStop: true, d.IsStopped ? AttentionGroup : RunningGroup));
        }

        foreach (var task in ideTasks)
        {
            entries.Add(new RunningEntry(
                $"idetask:{task.Pane}", RunningEntryKind.IdeTask,
                task.Status.Length > 0 ? task.Status : "ビルド・テスト", task.Pane,
                null, RunningEntryTone.Normal, CanStop: false, RunningGroup));
        }

        if (aiBusy)
        {
            entries.Add(new RunningEntry(
                "ai", RunningEntryKind.Ai, "AI ペイン", string.IsNullOrWhiteSpace(aiStatus) ? "応答中" : aiStatus!,
                null, RunningEntryTone.Normal, CanStop: false, RunningGroup));
        }

        foreach (var server in languageServers)
        {
            if (server.State is LspServerRuntimeState.Stopped or LspServerRuntimeState.Unconfigured)
                continue;
            var name = Path.GetFileNameWithoutExtension(server.Executable);
            var folder = WorkspacePaths.FolderName(server.Root);
            entries.Add(new RunningEntry(
                $"lsp:{server.Executable}|{server.Root}", RunningEntryKind.LanguageServer,
                name, $"{folder} · {StateLabel(server)}",
                null, server.State == LspServerRuntimeState.Failed ? RunningEntryTone.Failed : RunningEntryTone.Quiet,
                CanStop: false, ResidentGroup,
                server.LastError is { Length: > 0 } error ? $"{server.Root}{Environment.NewLine}{error}" : server.Root));
        }

        return entries
            .OrderBy(e => GroupOrder(e.Group))
            .ToList();
    }

    public static int GroupOrder(string group) => group switch
    {
        AttentionGroup => 0,
        RunningGroup => 1,
        SnoozedGroup => 3,
        _ => 2,
    };

    /// <summary>「あとで」にした時点の状態の鍵。これが変わったら（入力待ち→作業中→また入力待ち、停止→再開…）自動で戻す。
    /// 経過は入れない（時間が経つだけでは戻さない）。</summary>
    public static string SnoozeSignature(RunningEntry entry) => $"{entry.Group}|{entry.Tone}|{entry.Detail}";

    /// <summary>
    /// 「あとで」に回した行を一番下の「あとで」へ移し、状態が変わった行は「あとで」から外す（§24.71・iTerm2 3.7 の Snooze）。
    /// <paramref name="snoozed"/> は鍵 → あとでにした時点の <see cref="SnoozeSignature"/>。外したもの・もう居ないものはここから消す。
    /// </summary>
    public static IReadOnlyList<RunningEntry> ApplySnooze(IReadOnlyList<RunningEntry> entries, IDictionary<string, string> snoozed)
    {
        if (snoozed.Count == 0)
            return entries;
        var present = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<RunningEntry>(entries.Count);
        foreach (var entry in entries)
        {
            present.Add(entry.Key);
            if (snoozed.TryGetValue(entry.Key, out var signature))
            {
                if (signature == SnoozeSignature(entry))
                {
                    result.Add(entry with { Group = SnoozedGroup, Tone = RunningEntryTone.Quiet });
                    continue;
                }
                snoozed.Remove(entry.Key);
            }
            result.Add(entry);
        }
        foreach (var gone in snoozed.Keys.Where(k => !present.Contains(k)).ToList())
            snoozed.Remove(gone);
        return result.OrderBy(e => GroupOrder(e.Group)).ToList();
    }

    public static string StateLabel(LspServerRuntimeStatus server) => server.State switch
    {
        LspServerRuntimeState.Starting => "起動中",
        LspServerRuntimeState.Initializing => "初期化中",
        LspServerRuntimeState.ProjectLoading => "プロジェクト読み込み中",
        LspServerRuntimeState.Ready => "準備完了",
        LspServerRuntimeState.Reconnecting => $"再接続中（{server.ReconnectAttempt} 回目）",
        LspServerRuntimeState.Failed => "起動に失敗",
        _ => server.State.ToString(),
    };
}
