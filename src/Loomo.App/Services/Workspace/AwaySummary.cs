using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
}

/// <summary>留守中に起きたこと1件。<see cref="Payload"/> は行を押したときに開く対象
/// （コマンド行の文字列／<see cref="GitReflogEntry"/>／<see cref="AwayFileChange"/>）。</summary>
public sealed record AwayItem(AwayItemKind Kind, string Title, string Detail, string? Badge, bool IsFailure, object Payload);

/// <summary>留守中に作業ツリーで変わったファイル（リポジトリルート基準の相対パスと、その変更の項目）。</summary>
public sealed record AwayFileChange(string FullPath, string RelativePath, GitChangeEntry Entry, DateTime LastWriteUtc);

public sealed record AwaySummary(DateTime SinceUtc, DateTime ReturnedUtc, IReadOnlyList<AwayItem> Items, int HiddenCount)
{
    public bool IsEmpty => Items.Count == 0;

    /// <summary>見出し：どれだけ離れていて、何が何件起きたか。</summary>
    public string Header => $"離れていた {AwaySummaryBuilder.Duration(ReturnedUtc - SinceUtc)}のあいだに";

    public string Counts
    {
        get
        {
            var parts = new List<string>();
            int commands = Items.Count(i => i.Kind == AwayItemKind.Command);
            int failed = Items.Count(i => i.Kind == AwayItemKind.Command && i.IsFailure);
            int git = Items.Count(i => i.Kind == AwayItemKind.Git);
            int files = Items.Count(i => i.Kind == AwayItemKind.File);
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

    private static string Clock(DateTime utc)
        => utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
}
