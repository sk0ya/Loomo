using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>可視ターミナルで実行し終えたコマンド1回分の出力（OSC 133 の C〜D）。§24.21。
/// <paramref name="HeadOmitted"/> は先頭が欠けていること——スクロールバックの上限から押し出されたか、
/// <see cref="TerminalCommandOutputs.MaxChars"/> を超えて頭を落としたか。</summary>
public sealed record TerminalCommandOutput(
    string Command, string Text, int? ExitCode, DateTime FinishedUtc, bool HeadOmitted);

/// <summary>
/// コマンドごとに「前回」と「今回」の出力を持つ置き場（純ロジック・テスト対象）。§24.21。
/// <para><b>永続化しない。</b>出力は大きくなりうる素材で、ワークスペースの状態ファイルに入れると
/// §23.3.1 の 72MB 事故（起動不能）と同じ形になる。ターミナルのスクロールバック自体も再起動で消えるので、
/// 終了で消えるのはその延長として自然。</para>
/// <para><b>上限は3段</b>：1回分の文字数（<see cref="MaxChars"/>。超えたら<b>末尾を残す</b>——テストの集計や
/// ビルドのエラーは末尾に出る）、コマンドの種類（<see cref="MaxCommands"/>。古く使ったものから捨てる）、
/// 1コマンドにつき2回分（比べる相手は直前の1回で足りる）。</para>
/// </summary>
public sealed class TerminalCommandOutputs
{
    /// <summary>1回分の上限（文字）。スクロールバック上限（1万行）の出力をおおむね丸ごと持てる量。</summary>
    public const int MaxChars = 1_000_000;

    /// <summary>覚えておくコマンドの種類。</summary>
    public const int MaxCommands = 20;

    private sealed record Entry(TerminalCommandOutput Latest, TerminalCommandOutput? Previous);

    // 先頭ほど新しい。件数が小さいので線形探索で足りる。
    private readonly List<Entry> _entries = new();

    /// <summary>1回分を積む。同じコマンド行（序数比較）の今回は前回へ繰り下がる。空のコマンドは無視。</summary>
    public void Record(TerminalCommandOutput output)
    {
        if (RecentTerminalCommands.Normalize(output.Command) is not { } command)
            return;
        output = Clip(output with { Command = command });
        var index = _entries.FindIndex(e => string.Equals(e.Latest.Command, command, StringComparison.Ordinal));
        TerminalCommandOutput? previous = null;
        if (index >= 0)
        {
            previous = _entries[index].Latest;
            _entries.RemoveAt(index);
        }
        _entries.Insert(0, new Entry(output, previous));
        if (_entries.Count > MaxCommands)
            _entries.RemoveRange(MaxCommands, _entries.Count - MaxCommands);
    }

    /// <summary>今回の出力（新しい順）。</summary>
    public IReadOnlyList<TerminalCommandOutput> Latest => _entries.Select(e => e.Latest).ToList();

    /// <summary>前回と今回が揃っているもの（新しい順）。</summary>
    public IReadOnlyList<(TerminalCommandOutput Previous, TerminalCommandOutput Latest)> Pairs
        => _entries.Where(e => e.Previous is not null).Select(e => (e.Previous!, e.Latest)).ToList();

    public bool IsEmpty => _entries.Count == 0;

    private static TerminalCommandOutput Clip(TerminalCommandOutput output)
    {
        if (output.Text.Length <= MaxChars)
            return output;
        var tail = output.Text[^MaxChars..];
        // 行の途中から始めない（差分の先頭が欠けた1行になって必ず「変更あり」に見えるのを避ける）
        var newline = tail.IndexOf('\n');
        if (newline >= 0 && newline < tail.Length - 1)
            tail = tail[(newline + 1)..];
        return output with { Text = tail, HeadOmitted = true };
    }

    /// <summary>Diff へ送る素材。左＝前回・右＝今回（git 差分と同じ「旧→新」の向き）。</summary>
    public static DiffComparison Compare(TerminalCommandOutput previous, TerminalCommandOutput latest)
        => new(SideTitle("前回", previous), previous.Text, SideTitle("今回", latest), latest.Text);

    /// <summary>比較の左右の見出し。差分の帯とタブ名に出るので、いつの・成否どちらの実行かまで言う。</summary>
    public static string SideTitle(string label, TerminalCommandOutput output)
    {
        var time = output.FinishedUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var badge = RecentTerminalCommands.Badge(output.ExitCode);
        var omitted = output.HeadOmitted ? " 先頭省略" : "";
        return badge.Length == 0 ? $"{label} {time}{omitted}" : $"{label} {time} {badge}{omitted}";
    }

    /// <summary>エディタで開くときのタブ名。コマンドの先頭語と時刻で見分ける（全文はタブ名に長すぎる）。</summary>
    public static string DocumentName(TerminalCommandOutput output)
    {
        var head = RecentTerminalCommands.Title(output.Command);
        var word = head.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "output";
        var safe = new string(word.Select(c => Array.IndexOf(System.IO.Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c).ToArray());
        var time = output.FinishedUtc.ToLocalTime().ToString("HHmmss", CultureInfo.InvariantCulture);
        return $"出力-{safe}-{time}.log";
    }

    /// <summary>パレットの右の欄。選ぶと何が起きるかと、2回の違いの手がかり（成否・行数）。</summary>
    public static string PairDetail(TerminalCommandOutput previous, TerminalCommandOutput latest)
        => $"{RecentTerminalCommands.Title(latest.Command)}{Environment.NewLine}{Environment.NewLine}"
           + $"左: {SideTitle("前回", previous)}（{LineCount(previous.Text)} 行）{Environment.NewLine}"
           + $"右: {SideTitle("今回", latest)}（{LineCount(latest.Text)} 行）{Environment.NewLine}{Environment.NewLine}"
           + "Enter で前回の出力と今回の出力を Diff で比較";

    public static string OutputDetail(TerminalCommandOutput output)
        => $"{RecentTerminalCommands.Title(output.Command)}{Environment.NewLine}{Environment.NewLine}"
           + $"{SideTitle("実行", output)}（{LineCount(output.Text)} 行）{Environment.NewLine}{Environment.NewLine}"
           + "Enter で出力をエディタで開く（読むための写し・保存しても残らない）";

    public static int LineCount(string text)
        => text.Length == 0 ? 0 : text.Count(c => c == '\n') + 1;
}
