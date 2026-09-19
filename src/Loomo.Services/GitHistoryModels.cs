namespace sk0ya.Loomo.Services;

/// <summary>git log --graph の1行。枝の継続行では Hash が null。</summary>
public sealed record GitLogRow(
    string Graph,
    string? Hash,
    string? ShortHash,
    string? Author,
    string? Date,
    string? Refs,
    string? Subject)
{
    public bool IsCommit => Hash is not null;

    /// <summary>
    /// 行のツールチップ。先頭列はグラフ・refs・件名を1セルに詰めているので、列幅の内側に収まらない
    /// 限り<b>件名は途中で切れる</b>——列境界をドラッグして読むしかない状態を作らないための全文。
    /// refs があれば2行目に添える（これも同じセルで切れる側）。
    /// 枝の継続行は出すものが無いので null＝ツールチップ自体を出さない。
    /// </summary>
    public string? ToolTipText
    {
        get
        {
            if (Subject is not { Length: > 0 }) return null;
            return Refs is { Length: > 0 } refs ? $"{Subject}\n{refs}" : Subject;
        }
    }
}

/// <summary>スタッシュ1件。</summary>
public sealed record GitStashEntry(string Ref, string Description);
