namespace sk0ya.Loomo.App.Services;

internal enum ClaudeCodeStatus { Working, Idle }

/// <summary>ターミナルのタイトル（OSC 0）から Claude Code の状態を読む（§24.1.1）。
/// Claude Code は自分のタイトルの頭に状態の印を付ける——作業中は ◐／◑ を約1秒ごとに交互に、
/// ターンが終わって入力を待つ間は ✳。設定もフックも要らず、どの端末にも届く唯一の合図なので、
/// これを正本にする（Claude Code 2.1.302 を ConPTY 越しに採取して確認）。</summary>
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
}
