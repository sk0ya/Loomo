using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace sk0ya.Loomo.App.Services;

/// <summary>可視ターミナルで人間が実行し終えた1回分（コマンド行と終了コード）。</summary>
public sealed record RecentTerminalCommandRun(string Command, int? ExitCode, DateTime FinishedUtc);

/// <summary>
/// 「最近のコマンド」一覧の更新（純ロジック・テスト対象）。§24.20。
/// <para><b>更新は copy-on-write。</b><see cref="RecentUsageService"/> と同じ理由で、共有リストをその場で
/// 書き換えず、新しいリスト／新しい要素を組んで参照だけ差し替える（保存の直列化と取り合わない）。</para>
/// </summary>
public static class RecentTerminalCommands
{
    /// <summary>ワークスペースごとに残す件数。パレットで流し見できる量に留める。</summary>
    public const int MaxCount = 50;

    /// <summary>
    /// <paramref name="run"/> を先頭へ積んだ新しい一覧を返す。同じコマンド行（序数比較）は1件にまとめ、
    /// 実行回数を足して終了コード・時刻を最新に差し替える。空のコマンドは記録しない（null を返す＝変更なし）。
    /// </summary>
    public static List<RecentTerminalCommandSnapshot>? Record(
        IReadOnlyList<RecentTerminalCommandSnapshot>? current, RecentTerminalCommandRun run)
    {
        var command = Normalize(run.Command);
        if (command is null)
            return null;

        current ??= Array.Empty<RecentTerminalCommandSnapshot>();
        var previous = current.FirstOrDefault(c => string.Equals(c.Command, command, StringComparison.Ordinal));
        var updated = new List<RecentTerminalCommandSnapshot>(Math.Min(current.Count + 1, MaxCount))
        {
            new()
            {
                Command = command,
                ExitCode = run.ExitCode,
                LastRunUtc = run.FinishedUtc,
                RunCount = (previous?.RunCount ?? 0) + 1,
            },
        };
        updated.AddRange(current
            .Where(c => !string.IsNullOrWhiteSpace(c.Command)
                        && !string.Equals(c.Command, command, StringComparison.Ordinal))
            .Take(MaxCount - 1));
        return updated;
    }

    /// <summary>まとめて積む（裏のワークスペースで終わった実行を、そのワークスペースへ戻ったときに反映する）。
    /// 古い順に渡すこと。1件も積めなければ null。</summary>
    public static List<RecentTerminalCommandSnapshot>? RecordAll(
        IReadOnlyList<RecentTerminalCommandSnapshot>? current, IEnumerable<RecentTerminalCommandRun> runs)
    {
        List<RecentTerminalCommandSnapshot>? result = null;
        foreach (var run in runs)
            result = Record(result ?? current, run) ?? result;
        return result;
    }

    /// <summary>前後の空白を落とす。空なら null。改行は残す（複数行のコマンドはそのまま選び直せるように）。</summary>
    public static string? Normalize(string? command)
    {
        var trimmed = command?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>複数行か（プロンプトへ直接送ると最初の改行で走ってしまうので、コンポーザへ回す）。</summary>
    public static bool IsMultiline(string command) => command.IndexOf('\n') >= 0 || command.IndexOf('\r') >= 0;

    /// <summary>一覧の1行に出す形（改行を ⏎ に畳む）。</summary>
    public static string Title(string command)
        => string.Join(" ⏎ ", command.Replace("\r\n", "\n").Split('\n', '\r').Select(l => l.Trim())
            .Where(l => l.Length > 0));

    /// <summary>行の右端に添える成否。終了コードが取れなかった実行は何も出さない。</summary>
    public static string Badge(int? exitCode) => exitCode switch
    {
        null => "",
        0 => "✓",
        var code => $"✗ {code}",
    };

    /// <summary>プレビュー欄の説明文。選ぶと何が起きるか（送るだけで実行しない）をここで言い切る。</summary>
    public static string Detail(RecentTerminalCommandSnapshot item, DateTime nowUtc)
    {
        var exit = item.ExitCode switch
        {
            null => "終了コード: 不明",
            0 => "終了コード: 0（成功）",
            var code => $"終了コード: {code}（失敗）",
        };
        var when = item.LastRunUtc == default
            ? "最終実行: 不明"
            : $"最終実行: {item.LastRunUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)}"
              + $"（{Ago(nowUtc - item.LastRunUtc)}）";
        var action = IsMultiline(item.Command)
            ? "Enter でコンポーザへ送る（複数行のため・実行はしない）"
            : "Enter でターミナルへ送る（実行はしない）";
        var body = IsMultiline(item.Command)
            ? item.Command.Replace("\r\n", "\n").Replace("\n", Environment.NewLine)
              + Environment.NewLine + Environment.NewLine
            : "";
        return $"{body}{exit}{Environment.NewLine}{when}{Environment.NewLine}実行回数: {Math.Max(1, item.RunCount)}"
               + $"{Environment.NewLine}{Environment.NewLine}{action}";
    }

    private static string Ago(TimeSpan elapsed) => elapsed switch
    {
        { TotalMinutes: < 1 } => "たった今",
        { TotalHours: < 1 } => $"{(int)elapsed.TotalMinutes} 分前",
        { TotalDays: < 1 } => $"{(int)elapsed.TotalHours} 時間前",
        _ => $"{(int)elapsed.TotalDays} 日前",
    };
}

/// <summary>
/// 1つのターミナルタブで「コマンドが実行されて終わった」を組み立てる状態機械（純ロジック・テスト対象）。
/// <para>コマンド行は OSC 633;E（<c>TerminalTabView.CommandHistoryRecorded</c>）、成否は OSC 133 の
/// C／D（<c>ShellCommandActivity</c>）で別々に届く。E→C→D の順に揃ったときだけ1回分として返す。
/// C を見ていない D（シェル統合の初期化・プロンプト再描画）は §24.1 の偽バッジと同じ理由で捨てる。</para>
/// <para>sk0ya.Terminal.Controls の現行版は、直前と同じコマンドを続けて打つと履歴側でまとめて
/// <c>CommandHistoryRecorded</c> を出さない。そのとき古いコマンド行を使い回すと<b>別の実行</b>を記録しかねないので、
/// 行は D／次のプロンプトで捨て、届かなかった回は記録しない（安全側）。ライブラリが C／D に
/// コマンド行を載せる版（<c>ShellCommandActivityEventArgs.CommandLine</c>）になれば、その値を
/// <paramref name="commandLine"/> に渡すだけで連続実行も毎回記録できる。</para>
/// </summary>
public sealed class TerminalCommandRunTracker
{
    private string? _reported;
    private bool _executed;

    /// <summary>E（コマンド行）。C より先に届く。</summary>
    public void OnCommandLine(string? command) => _reported = RecentTerminalCommands.Normalize(command);

    public void OnPromptStart()
    {
        _reported = null;
        _executed = false;
    }

    public void OnExecuted(string? commandLine = null)
    {
        if (RecentTerminalCommands.Normalize(commandLine) is { } line)
            _reported = line;
        _executed = _reported is not null;
    }

    /// <summary>D。E→C を見ていれば1回分を返す。</summary>
    public RecentTerminalCommandRun? OnDone(int? exitCode, DateTime nowUtc, string? commandLine = null)
    {
        var command = RecentTerminalCommands.Normalize(commandLine) ?? (_executed ? _reported : null);
        _reported = null;
        _executed = false;
        return command is null ? null : new RecentTerminalCommandRun(command, exitCode, nowUtc);
    }
}
