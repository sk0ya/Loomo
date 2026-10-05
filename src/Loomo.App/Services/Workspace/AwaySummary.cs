using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 人が部屋を離れていたかの判定（純ロジック・テスト対象）。§24.22。
/// <para>離れ方は2通り：<b>Loomo から別のアプリへ移った</b>（ウィンドウの非アクティブ化）と、
/// <b>Loomo を前面に置いたまま席を立った</b>（無操作。システム全体の最終入力時刻で見る）。
/// どちらも、戻ってきた（再アクティブ化・ウィンドウ内での入力）時点で離れていた長さを測り、
/// <see cref="MinAway"/> 以上なら「離れていた」として離れた時刻を返す。</para>
/// </summary>
public sealed class PresenceTracker
{
    /// <summary>これより短い離席は知らせない（ちょっとブラウザを見に行った程度では出さない）。</summary>
    public static readonly TimeSpan MinAway = TimeSpan.FromMinutes(5);

    private DateTime? _awaySinceUtc;

    /// <summary>いま離れている扱いか（離れた時刻が決まっているか）。</summary>
    public bool IsAway => _awaySinceUtc is not null;

    /// <summary>Loomo から別のアプリへ移った。すでに離れている（無操作で離席済み）なら、早い方を残す。</summary>
    public void OnDeactivated(DateTime nowUtc) => _awaySinceUtc ??= nowUtc;

    /// <summary>前面のまま無操作が続いているかの見回り。最後の入力から <see cref="MinAway"/> 経っていれば、
    /// その入力の時刻から離れていたとみなす。</summary>
    public void OnIdleCheck(DateTime nowUtc, DateTime lastInputUtc)
    {
        if (_awaySinceUtc is null && nowUtc - lastInputUtc >= MinAway)
            _awaySinceUtc = lastInputUtc;
    }

    /// <summary>戻ってきた（再アクティブ化・入力）。<see cref="MinAway"/> 以上離れていたなら離れた時刻、
    /// そうでなければ null（短い離席は数えず、状態も戻す）。</summary>
    public DateTime? OnReturn(DateTime nowUtc)
    {
        if (_awaySinceUtc is not { } since)
            return null;
        _awaySinceUtc = null;
        return nowUtc - since >= MinAway ? since : null;
    }
}

public enum AwayItemKind
{
    Command,
    Git,
    File,
    /// <summary>しおり（§27.13）。この日のまとめ（§24.23）にだけ並ぶ。</summary>
    Bookmark,
}

/// <summary>留守中に起きたこと1件。<see cref="Payload"/> は行を押したときに開く対象
/// （コマンド行の文字列／<see cref="GitReflogEntry"/>／<see cref="AwayFileChange"/>／<see cref="TrailNoteRecord"/>）。</summary>
public sealed record AwayItem(AwayItemKind Kind, string Title, string Detail, string? Badge, bool IsFailure, object Payload);

/// <summary>留守中に作業ツリーで変わったファイル（リポジトリルート基準の相対パスと、その変更の項目）。</summary>
public sealed record AwayFileChange(string FullPath, string RelativePath, GitChangeEntry Entry, DateTime LastWriteUtc);

/// <summary>カード1枚分のまとめ。留守中（§24.22）と、この日のまとめ（§24.23）が同じ形を使う——
/// <paramref name="Title"/> を渡せばその見出し、無ければ「離れていた N 分のあいだに」。</summary>
public sealed record AwaySummary(DateTime SinceUtc, DateTime ReturnedUtc, IReadOnlyList<AwayItem> Items, int HiddenCount,
    string? Title = null)
{
    public bool IsEmpty => Items.Count == 0;

    /// <summary>見出し：どれだけ離れていて、何が何件起きたか。</summary>
    public string Header => Title ?? $"離れていた {AwaySummaryBuilder.Duration(ReturnedUtc - SinceUtc)}のあいだに";

    public string Counts
    {
        get
        {
            var parts = new List<string>();
            int commands = Items.Count(i => i.Kind == AwayItemKind.Command);
            int failed = Items.Count(i => i.Kind == AwayItemKind.Command && i.IsFailure);
            int git = Items.Count(i => i.Kind == AwayItemKind.Git);
            int files = Items.Count(i => i.Kind == AwayItemKind.File);
            int bookmarks = Items.Count(i => i.Kind == AwayItemKind.Bookmark);
            if (bookmarks > 0)
                parts.Add($"しおり {bookmarks}");
            if (commands > 0)
                parts.Add(failed > 0 ? $"コマンド {commands}（失敗 {failed}）" : $"コマンド {commands}");
            if (git > 0)
                parts.Add($"Git {git}");
            if (files > 0)
                parts.Add($"ファイル {files}");
            if (HiddenCount > 0)
                parts.Add($"ほか {HiddenCount}");
            return string.Join("・", parts);
        }
    }

    /// <summary>日報の下書きなどに貼るための Markdown（カードに並んでいる行そのまま）。</summary>
    public string ToMarkdown()
    {
        var text = new StringBuilder();
        text.Append("## ").AppendLine(Header);
        foreach (var group in Items.GroupBy(i => i.Kind))
        {
            text.AppendLine();
            text.Append("### ").AppendLine(group.Key switch
            {
                AwayItemKind.Bookmark => "しおり",
                AwayItemKind.Command => "コマンド",
                AwayItemKind.Git => "Git",
                _ => "ファイル",
            });
            foreach (var item in group)
            {
                var title = item.Kind == AwayItemKind.Command ? $"`{item.Title}`" : item.Title;
                text.Append("- ").Append(title);
                if (!string.IsNullOrEmpty(item.Badge))
                    text.Append(' ').Append(item.Badge);
                text.Append(" — ").AppendLine(item.Detail);
            }
        }
        if (HiddenCount > 0)
            text.AppendLine().AppendLine($"（ほか {HiddenCount} 件）");
        return text.ToString();
    }
}

/// <summary>
/// 「離れた時刻 T 以降に起きたこと」をまとめる（純ロジック・テスト対象）。§24.22。
/// <para><b>離れた瞬間のスナップショットは取らない。</b>無操作の離席は後から分かる（5分経って初めて
/// 「5分前に離れていた」と決まる）ので、その瞬間に状態を写すことはできない。代わりに戻った時点で、
/// 時刻を持つ記録——最近のコマンドの終了時刻・HEAD の reflog の時刻・変更中ファイルの更新時刻——を
/// T で切る。</para>
/// </summary>
public static class AwaySummaryBuilder
{
    public const int MaxCommands = 6;
    public const int MaxGit = 5;
    public const int MaxFiles = 8;

    public static AwaySummary Build(
        DateTime sinceUtc,
        DateTime returnedUtc,
        IEnumerable<RecentTerminalCommandSnapshot> commands,
        IEnumerable<GitReflogEntry> headReflog,
        IEnumerable<AwayFileChange> files)
    {
        var items = new List<AwayItem>();
        int hidden = 0;

        // 失敗を先に（戻って最初に知りたいのはそれ）、同じ成否の中では新しい順
        var ranCommands = commands
            .Where(c => !string.IsNullOrWhiteSpace(c.Command) && c.LastRunUtc > sinceUtc)
            .OrderByDescending(c => c.ExitCode is { } code && code != 0)
            .ThenByDescending(c => c.LastRunUtc)
            .ToList();
        hidden += Math.Max(0, ranCommands.Count - MaxCommands);
        items.AddRange(ranCommands.Take(MaxCommands).Select(c => new AwayItem(
            AwayItemKind.Command,
            RecentTerminalCommands.Title(c.Command),
            $"{Clock(c.LastRunUtc)} に終了",
            RecentTerminalCommands.Badge(c.ExitCode) is { Length: > 0 } badge ? badge : null,
            c.ExitCode is { } exit && exit != 0,
            c.Command)));

        var operations = headReflog
            .Where(e => e.Time is { } time && time.UtcDateTime > sinceUtc)
            .OrderByDescending(e => e.Time)
            .ToList();
        hidden += Math.Max(0, operations.Count - MaxGit);
        items.AddRange(operations.Take(MaxGit).Select(e => new AwayItem(
            AwayItemKind.Git,
            string.IsNullOrWhiteSpace(e.Description) ? e.Message : e.Description,
            $"{Clock(e.Time!.Value.UtcDateTime)}・{e.ShortHash}" + (string.IsNullOrWhiteSpace(e.Subject) ? "" : $"・{e.Subject}"),
            null,
            false,
            e)));

        var changed = files
            .Where(f => f.LastWriteUtc > sinceUtc)
            .OrderByDescending(f => f.LastWriteUtc)
            .ToList();
        hidden += Math.Max(0, changed.Count - MaxFiles);
        items.AddRange(changed.Take(MaxFiles).Select(f => new AwayItem(
            AwayItemKind.File,
            f.RelativePath,
            $"{Clock(f.LastWriteUtc)} に更新・{StatusLabel(f.Entry)}",
            null,
            false,
            f)));

        return new AwaySummary(sinceUtc, returnedUtc, items, hidden);
    }

    /// <summary>変更の種類（一覧の右の短い説明）。</summary>
    public static string StatusLabel(GitChangeEntry entry)
    {
        if (entry.IsConflicted) return "競合";
        if (entry.IsUntracked) return "新規（未追跡）";
        char status = entry.WorkStatus is ' ' or '.' ? entry.IndexStatus : entry.WorkStatus;
        return status switch
        {
            'A' => "追加",
            'D' => "削除",
            'R' => "名前変更",
            _ => "変更",
        };
    }

    public static string Duration(TimeSpan span) => span switch
    {
        { TotalHours: < 1 } => $"{Math.Max(1, (int)span.TotalMinutes)} 分",
        { TotalDays: < 1 } => span.Minutes == 0 ? $"{(int)span.TotalHours} 時間" : $"{(int)span.TotalHours} 時間 {span.Minutes} 分",
        _ => $"{(int)span.TotalDays} 日",
    };

    internal static string Clock(DateTime utc)
        => utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>
/// 「この日に部屋で起きたこと」をまとめる（純ロジック・テスト対象）。§24.23。
/// <para>留守中のまとめ（<see cref="AwaySummaryBuilder"/>）を「離れた時刻以降」から<b>ローカル日付1日の範囲</b>へ
/// 広げたもの。材料は時刻を持つ記録だけで、まとめそのものは保存しない（開くたびに組み立てる）：
/// しおり（trail.db）、コマンドの実行記録（trail.db の command_runs——最近のコマンドは同じ行を1件に
/// まとめてしまうので使えない）、HEAD の reflog、作業ツリーで変更中のファイルの更新時刻。</para>
/// </summary>
public static class DaySummaryBuilder
{
    public const int MaxBookmarks = 10;
    public const int MaxCommands = 10;
    public const int MaxGit = 10;
    public const int MaxFiles = 10;

    public static AwaySummary Build(
        DateOnly day,
        IEnumerable<TrailNoteRecord> bookmarks,
        IEnumerable<CommandRunRecord> commandRuns,
        IEnumerable<GitReflogEntry> headReflog,
        IEnumerable<AwayFileChange> files)
    {
        var start = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
        var end = start.AddDays(1);
        bool InDay(DateTime local) => local >= start && local < end;

        var items = new List<AwayItem>();
        int hidden = 0;

        // しおり：その日に人が自分で言葉を残した地点。記録ではなく本人のメモなので先頭に置く。
        var notes = bookmarks.Where(b => b.Day == day).OrderByDescending(b => b.Timestamp).ToList();
        hidden += Math.Max(0, notes.Count - MaxBookmarks);
        items.AddRange(notes.Take(MaxBookmarks).Select(b => new AwayItem(
            AwayItemKind.Bookmark,
            b.Note,
            $"{Clock(b.Timestamp)}・{b.Label}",
            null,
            false,
            b)));

        // コマンド：同じ行は1件にまとめて回数と失敗数を添える。最後の実行が失敗のままのものを先に。
        var commands = commandRuns
            .Where(r => !string.IsNullOrWhiteSpace(r.Command) && InDay(r.Timestamp))
            .GroupBy(r => r.Command, StringComparer.Ordinal)
            .Select(g =>
            {
                var runs = g.OrderBy(r => r.Timestamp).ToList();
                return (Command: g.Key, First: runs[0].Timestamp, Last: runs[^1],
                    Count: runs.Count, Failed: runs.Count(r => r.ExitCode is { } code && code != 0));
            })
            .OrderByDescending(c => c.Last.ExitCode is { } code && code != 0)
            .ThenByDescending(c => c.Last.Timestamp)
            .ToList();
        hidden += Math.Max(0, commands.Count - MaxCommands);
        items.AddRange(commands.Take(MaxCommands).Select(c => new AwayItem(
            AwayItemKind.Command,
            RecentTerminalCommands.Title(c.Command),
            CommandDetail(c.First, c.Last.Timestamp, c.Count, c.Failed),
            RecentTerminalCommands.Badge(c.Last.ExitCode) is { Length: > 0 } badge ? badge : null,
            c.Last.ExitCode is { } exit && exit != 0,
            c.Command)));

        var operations = headReflog
            .Where(e => e.Time is { } time && InDay(time.LocalDateTime))
            .OrderByDescending(e => e.Time)
            .ToList();
        hidden += Math.Max(0, operations.Count - MaxGit);
        items.AddRange(operations.Take(MaxGit).Select(e => new AwayItem(
            AwayItemKind.Git,
            string.IsNullOrWhiteSpace(e.Description) ? e.Message : e.Description,
            $"{Clock(e.Time!.Value.LocalDateTime)}・{e.ShortHash}" + (string.IsNullOrWhiteSpace(e.Subject) ? "" : $"・{e.Subject}"),
            null,
            false,
            e)));

        // ファイル：いまも変更中で、最後に書かれたのがその日のもの（コミット済みの分は Git の行に入る）。
        var changed = files
            .Where(f => InDay(f.LastWriteUtc.ToLocalTime()))
            .OrderByDescending(f => f.LastWriteUtc)
            .ToList();
        hidden += Math.Max(0, changed.Count - MaxFiles);
        items.AddRange(changed.Take(MaxFiles).Select(f => new AwayItem(
            AwayItemKind.File,
            f.RelativePath,
            $"{Clock(f.LastWriteUtc.ToLocalTime())} に更新・{AwaySummaryBuilder.StatusLabel(f.Entry)}",
            null,
            false,
            f)));

        return new AwaySummary(start.ToUniversalTime(), end.ToUniversalTime(), items, hidden,
            $"{day.ToString("M/d (ddd)", CultureInfo.CurrentCulture)} のまとめ");
    }

    private static string CommandDetail(DateTime first, DateTime last, int count, int failed)
    {
        if (count == 1)
            return $"{Clock(last)} に終了";
        var span = Clock(first) == Clock(last) ? Clock(last) : $"{Clock(first)}〜{Clock(last)}";
        return failed > 0 ? $"{span}・{count} 回（失敗 {failed}）" : $"{span}・{count} 回";
    }

    private static string Clock(DateTime local) => local.ToString("HH:mm", CultureInfo.InvariantCulture);
}
