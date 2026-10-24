namespace sk0ya.Loomo.App.Services;

internal enum ClaudeCodeStatus { Working, Idle }

/// <summary>一覧での Claude の状態。並び順そのもの（人間の番が先）。</summary>
internal enum ClaudeAgentState { NeedsApproval, TurnEnded, Waiting, Working }

/// <summary>Claude Code が居るターミナルタブ1つ（§38.8）。</summary>
internal sealed record ClaudeAgentTab(
    Guid TabId, ClaudeAgentState State, string? Title, string? SessionId, string? TranscriptPath, long Stamp)
{
    public static ClaudeAgentState StateOf(ClaudeCodeStatus status, bool approval, bool unseenTurn)
        => approval ? ClaudeAgentState.NeedsApproval
            : status == ClaudeCodeStatus.Working ? ClaudeAgentState.Working
            : unseenTurn ? ClaudeAgentState.TurnEnded
            : ClaudeAgentState.Waiting;

    public static string Label(ClaudeAgentState state) => state switch
    {
        ClaudeAgentState.NeedsApproval => "● 承認待ち",
        ClaudeAgentState.TurnEnded => "✳ 入力待ち",
        ClaudeAgentState.Waiting => "待機",
        _ => "◐ 作業中",
    };
}

/// <summary>ターミナルのタイトル（OSC 0）から Claude Code の状態を読む（§24.1.1）。
/// Claude Code は自分のタイトルの頭に状態の印を付ける——作業中は ◐／◑ を約1秒ごとに交互に、
/// ターンが終わって入力を待つ間は ✳。設定もフックも要らず、どの端末にも届く合図なので、OSC 7501 の申告が無い間は
/// これを正本にする（Claude Code 2.1.302 を ConPTY 越しに採取して確認）。申告できる端末では印は付かない（§24.61）。</summary>
internal static class ClaudeCodeTitle
{
    private const char IdleMark = '✳';

    // ◐◑ は実測。◒◓ は同じ回転の残り2コマで、版によって使われても取りこぼさないよう入れておく。
    private static readonly char[] WorkingMarks = ['◐', '◑', '◒', '◓'];

    /// <summary>Claude Code のタイトルでなければ null。</summary>
    public static ClaudeCodeStatus? Parse(string? title)
    {
        if (string.IsNullOrEmpty(title) || title.Length < 2 || title[1] != ' ')
            return null;
        if (title[0] == IdleMark)
            return ClaudeCodeStatus.Idle;
        return Array.IndexOf(WorkingMarks, title[0]) >= 0 ? ClaudeCodeStatus.Working : null;
    }

    /// <summary>見出しに状態の印を付ける。OSC 7501 で状態を申告できる端末だと、Claude Code はタイトルに印を付けず
    /// 題だけを書く（2.1.310 を同梱の ConPTY 越しに採取して確認・2026-10-25）ので、申告から Loomo が付け直す（§24.61）。
    /// 既に印があればそのまま。</summary>
    public static string WithMark(string? title, ClaudeCodeStatus status)
    {
        var mark = status == ClaudeCodeStatus.Working ? WorkingMarks[0] : IdleMark;
        if (string.IsNullOrWhiteSpace(title))
            return mark.ToString();
        return Parse(title) is null ? $"{mark} {title.Trim()}" : title;
    }

    /// <summary>印を外した題（「◐ Fix src/foo.cs」→「Fix src/foo.cs」）。Claude のタイトルでなければ null。</summary>
    public static string? Subject(string? title)
    {
        if (Parse(title) is null) return null;
        var subject = title![2..].Trim();
        return subject.Length == 0 ? null : subject;
    }
}
