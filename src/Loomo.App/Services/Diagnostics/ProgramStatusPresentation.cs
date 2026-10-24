using Terminal.Tabs;

namespace sk0ya.Loomo.App.Services;

/// <summary>Claude Code が OSC 7501 で申告している状態（§24.61）。</summary>
/// <param name="Status">作業中か、手が空いているか（承認・質問を待っている間も手は空いている）。</param>
/// <param name="Blocked">人を待っている（許可・質問・認証）。</param>
/// <param name="Title">申告の題（あれば）。</param>
internal readonly record struct ClaudeProgramStatus(ClaudeCodeStatus Status, bool Blocked, ProgramStatusKind Kind, string? Title);

/// <summary>
/// ターミナルの中のプログラムが OSC 7501（Program Status Protocol）で申告した状態を、部屋の言葉に直す（純ロジック・§24.61）。
/// <para>画面（タイトルの印）を読んで推すのではなく本人の申告なので、申告があるうちは Claude Code の状態の正本にする
/// （タイトルの印・フックはフォールバック）。Claude 以外のプログラムも同じ口で拾い、「人の番」を動いているもの面と袖に出す。</para>
/// </summary>
internal static class ProgramStatusPresentation
{
    public const string ClaudeApp = "claude-code";

    /// <summary>Claude Code の申告。居なければ null。複数のレコード（サブエージェント等）があれば、人の番 → 作業中 →
    /// 手が空いている の順で強いものを採る。<c>done</c>／<c>error</c> は「ターンが終わって入力を待っている」——Claude Code 2.1.310 は
    /// ターンの終わりに <c>idle</c> ではなく <c>done</c>（終わった一言つき）を送り、終了時に <c>clear</c> する（2026-10-25 実測）。
    /// <paramref name="notBefore"/> より前の申告は数えない（シェルのコマンドが終わった後に残っている <c>done</c> は、<c>clear</c> を
    /// 送らずに落ちた Claude のもの）。</summary>
    public static ClaudeProgramStatus? Claude(IReadOnlyList<ProgramStatusRecord> records, DateTimeOffset notBefore = default)
    {
        var live = records
            .Where(r => r.App == ClaudeApp && r.UpdatedAt >= notBefore)
            .ToList();
        if (live.Count == 0)
            return null;
        var title = live.OrderBy(r => r.Id.Length).Select(r => r.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        if (live.FirstOrDefault(r => r.State == ProgramStatusState.Blocked) is { } blocked)
            return new ClaudeProgramStatus(ClaudeCodeStatus.Idle, true, blocked.Kind, title);
        return new ClaudeProgramStatus(
            live.Any(r => r.State == ProgramStatusState.Working) ? ClaudeCodeStatus.Working : ClaudeCodeStatus.Idle,
            false, ProgramStatusKind.None, title);
    }

    /// <summary>Claude 以外で、いま動いている（作業中・人を待っている）申告。動いているもの面に出す。</summary>
    public static IReadOnlyList<ProgramStatusRecord> Others(IReadOnlyList<ProgramStatusRecord> records)
        => records
            .Where(r => r.App != ClaudeApp && r.State is ProgramStatusState.Working or ProgramStatusState.Blocked)
            .ToList();

    /// <summary>行の題：申告の題 → プログラム名 → id → 「プログラム」。</summary>
    public static string Name(ProgramStatusRecord record)
        => !string.IsNullOrWhiteSpace(record.Title) ? record.Title!
            : record.App is { Length: > 0 } app ? (record.Id.Length > 0 ? $"{app} · {record.Id}" : app)
            : record.Id.Length > 0 ? record.Id
            : "プログラム";

    /// <summary>状態の言葉（「● 許可を待っています」「42%」）。申告の <c>msg</c> は意味を読まずに添えるだけ（仕様）。</summary>
    public static string StateLabel(ProgramStatusRecord record)
        => record.State switch
        {
            ProgramStatusState.Blocked => record.Kind switch
            {
                ProgramStatusKind.Permission => "● 許可を待っています",
                ProgramStatusKind.Question => "● 答えを待っています",
                ProgramStatusKind.Auth => "● 認証を待っています",
                _ => "● あなたを待っています",
            },
            ProgramStatusState.Working => record.Progress is { } p ? $"作業中 {p}%" : "作業中",
            ProgramStatusState.Done => "終わりました",
            ProgramStatusState.Error => "失敗しました",
            _ => "待機",
        };

    /// <summary>行の添え書き：状態の言葉と、申告の一言（あれば）。</summary>
    public static string Detail(ProgramStatusRecord record)
        => string.IsNullOrWhiteSpace(record.Message) ? StateLabel(record) : $"{StateLabel(record)} · {record.Message}";

    /// <summary>表示に効く中身が同じか（申告の時刻だけ違うものは同じとみなす）。</summary>
    public static bool SameShape(IReadOnlyList<ProgramStatusRecord> a, IReadOnlyList<ProgramStatusRecord> b)
        => a.Select(r => r with { UpdatedAt = default }).SequenceEqual(b.Select(r => r with { UpdatedAt = default }));
}
