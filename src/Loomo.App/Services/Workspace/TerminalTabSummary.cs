using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using sk0ya.Loomo.Core.Files;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 1 つのターミナルタブのいまの様子（§24.53）。TABS の行の 2 行目と、動いているもの面（§24.54）の行が同じものを読む。
/// <para>前面のコマンドは OSC 133 の C〜D の間（<see cref="RunningCommand"/>）、最後に終わったものは E→C→D で揃った 1 回分
/// （<see cref="LastRun"/>・§24.20 と同じ組み立て）。cwd はシェル統合が知らせる値、ブランチは cwd から <c>.git/HEAD</c> を読む。</para>
/// </summary>
public sealed record TerminalTabStatus(
    string? WorkingDirectory = null,
    GitHeadInfo? Head = null,
    string? RunningCommand = null,
    DateTime? RunningSinceUtc = null,
    RecentTerminalCommandRun? LastRun = null)
{
    public static readonly TerminalTabStatus Empty = new();

    /// <summary>前面でコマンドが走っているか（C を見て D をまだ見ていない）。</summary>
    public bool IsRunning => RunningSinceUtc is not null;
}

/// <summary>ターミナルタブの様子を 1 行に畳む（純ロジック・テスト対象・§24.53）。
/// 形は「場所 (ブランチ) · コマンド ✓ · 3分前」。走っている間は「▶ コマンド · 1分23秒」。</summary>
public static class TerminalTabSummary
{
    /// <summary>行に出すコマンドの長さの上限（それより長いものは … で切る。全文はツールチップ）。</summary>
    public const int MaxCommandChars = 48;

    public static string Line(TerminalTabStatus status, IReadOnlyList<string>? folders, DateTime nowUtc)
    {
        var parts = new List<string>(3);
        if (Place(status, folders) is { Length: > 0 } place)
            parts.Add(place);
        if (status.IsRunning)
        {
            var command = status.RunningCommand is { } running ? Shorten(running) + " · " : "";
            parts.Add($"▶ {command}{Elapsed(nowUtc - status.RunningSinceUtc!.Value)}");
        }
        else if (status.LastRun is { } last)
        {
            var badge = RecentTerminalCommands.Badge(last.ExitCode);
            parts.Add(badge.Length == 0 ? Shorten(last.Command) : $"{Shorten(last.Command)} {badge}");
            parts.Add(Ago(nowUtc - last.FinishedUtc));
        }
        return string.Join(" · ", parts);
    }

    /// <summary>ツールチップ：省略しない場所・ブランチ・コマンド・時刻。</summary>
    public static string Detail(TerminalTabStatus status, DateTime nowUtc)
    {
        var lines = new List<string>(4);
        if (status.WorkingDirectory is { Length: > 0 } cwd)
            lines.Add($"場所: {cwd}");
        if (status.Head is { } head)
            lines.Add(head.IsDetached ? $"ブランチ: (detached) {head.Branch}" : $"ブランチ: {head.Branch}");
        if (status.IsRunning)
        {
            lines.Add($"実行中: {status.RunningCommand ?? "（コマンド行は不明）"}");
            lines.Add($"経過: {Elapsed(nowUtc - status.RunningSinceUtc!.Value)}");
        }
        else if (status.LastRun is { } last)
        {
            lines.Add($"最後のコマンド: {last.Command}");
            lines.Add(last.ExitCode switch
            {
                null => "終了コード: 不明",
                0 => "終了コード: 0（成功）",
                var code => $"終了コード: {code}（失敗）",
            } + $"　{last.FinishedUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}（{Ago(nowUtc - last.FinishedUtc)}）");
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>「場所 (ブランチ)」。ワークスペースの中ならワークスペースからの相対（根ならフォルダー名）、
    /// 外ならフォルダー名だけ（行の幅に絶対パスは入らない）。</summary>
    public static string Place(TerminalTabStatus status, IReadOnlyList<string>? folders)
    {
        var place = PlaceOf(status.WorkingDirectory, folders);
        if (status.Head is not { } head)
            return place;
        var branch = head.IsDetached ? $"@{head.Branch}" : head.Branch;
        return place.Length == 0 ? $"({branch})" : $"{place} ({branch})";
    }

    private static string PlaceOf(string? cwd, IReadOnlyList<string>? folders)
    {
        if (string.IsNullOrWhiteSpace(cwd))
            return "";
        var display = WorkspacePaths.FolderFor(folders, cwd) is not null
            ? WorkspacePaths.ToDisplayPath(folders, cwd)
            : "";
        if (display.Length > 0)
            return display;
        return WorkspacePaths.FolderName(cwd);
    }

    /// <summary>1 行に畳み、長ければ切る（複数行のコマンドは ⏎ で繋ぐ——§24.20 と同じ見せ方）。</summary>
    public static string Shorten(string command)
    {
        var title = RecentTerminalCommands.Title(command);
        return title.Length <= MaxCommandChars ? title : title[..(MaxCommandChars - 1)] + "…";
    }

    /// <summary>走っている時間。1 分未満は秒、1 時間未満は「分秒」、それ以上は「時間分」。</summary>
    public static string Elapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return elapsed switch
        {
            { TotalMinutes: < 1 } => $"{(int)elapsed.TotalSeconds}秒",
            { TotalHours: < 1 } => $"{(int)elapsed.TotalMinutes}分{elapsed.Seconds:00}秒",
            _ => $"{(int)elapsed.TotalHours}時間{elapsed.Minutes:00}分",
        };
    }

    /// <summary>終わってからの時間。</summary>
    public static string Ago(TimeSpan elapsed) => elapsed switch
    {
        { TotalMinutes: < 1 } => "たった今",
        { TotalHours: < 1 } => $"{(int)elapsed.TotalMinutes}分前",
        { TotalDays: < 1 } => $"{(int)elapsed.TotalHours}時間前",
        _ => $"{(int)elapsed.TotalDays}日前",
    };
}
