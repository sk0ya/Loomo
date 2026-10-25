using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using sk0ya.Loomo.Core.Safety;

namespace sk0ya.Loomo.App.Services;

/// <summary>ペグボードへ残す1枚ぶん（本文・見出し・出典）。</summary>
public sealed record TerminalPegboardItem(string Content, string? Title, PegboardTerminalSource Source);

/// <summary>
/// ターミナルの出力・選択を<b>出典付き</b>のペグボード素材にする（純ロジック・テスト対象）。§24.26。
/// §24.24 のブラウザの抜き書きと同じ形：本文は text、出典（どのタブの・どのコマンドの・どこで・いつ・成否）を
/// 一緒に残し、カードの「出典を開く」でそのタブの該当箇所へ戻る。
/// <para><b>大きさに上限を持つ</b>（<see cref="MaxChars"/>）。ペグボードは workspaces.json に入るので、
/// 出力を丸ごと入れると §23.3.1 の 72MB 事故（起動不能）と同じ形になる——§24.21 が「ペグボードへ直接送る」を
/// 見送った理由そのもの。超えたら<b>末尾を残す</b>（テストの集計・ビルドのエラーは末尾に出る）。</para>
/// <para><b>秘密を伏せてから残す</b>（§24.66・<see cref="SecretRedactor"/>）。本文もコマンド行も状態ファイルに入るので、
/// トークンや鍵がそのまま再起動のあとまで残らないようにする。伏せた数は出典に持ってカードで言う。</para>
/// </summary>
public static class TerminalPegboardMaterial
{
    /// <summary>1枚の上限（文字）。数百行の出力が収まり、カードを何十枚溜めても状態ファイルが MB 単位で済む量。</summary>
    public const int MaxChars = 32_000;

    /// <summary>出典へ戻るときに探す目印の長さ。ターミナルの検索は行（折り返した1段）の中でしか当たらないので、
    /// 狭いペインで折り返されても1段に収まりやすい長さにする。</summary>
    public const int AnchorChars = 40;

    /// <summary>コマンド1回分の出力（§24.21 の切り出し）をペグボードの1枚にする。出力が空なら null。</summary>
    public static TerminalPegboardItem? FromOutput(TerminalCommandOutput output, string? tabTitle)
    {
        var (text, clipped) = Clip(output.Text.Trim('\r', '\n'));
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = SecretRedactor.Redact(text, out var maskedText);
        var command = SecretRedactor.Redact(output.Command, out var maskedCommand);
        var source = new PegboardTerminalSource
        {
            TabId = output.TabId,
            TabTitle = NullIfBlank(tabTitle),
            Command = command,
            MaskedSecrets = maskedText + maskedCommand,
            WorkingDirectory = NullIfBlank(output.WorkingDirectory),
            ExitCode = output.ExitCode,
            CapturedUtc = output.FinishedUtc,
            HeadOmitted = clipped || output.HeadOmitted,
        };
        // 見出しは出力の先頭行ではなく「どのコマンドの出力か」——先頭行は区切り線や空行のことが多く見分けがつかない。
        return new TerminalPegboardItem(text, $"{RecentTerminalCommands.Title(command)} の出力", source);
    }

    /// <summary>ターミナルの選択をペグボードの1枚にする。<paramref name="containing"/> は選択を含むコマンドの出力
    /// （<see cref="FindContaining"/>）——分かればそのコマンド・場所・成否・時刻を出典にし、分からなければタブと
    /// 残した時点の場所・時刻だけにする。選択が空なら null。</summary>
    public static TerminalPegboardItem? FromSelection(
        string selection, Guid tabId, string? tabTitle, string? workingDirectory,
        TerminalCommandOutput? containing, DateTime nowUtc)
    {
        var (text, clipped) = Clip(selection.Trim());
        if (text.Length == 0)
            return null;
        text = SecretRedactor.Redact(text, out var maskedText);
        var maskedCommand = 0;
        var command = containing?.Command is { } c ? SecretRedactor.Redact(c, out maskedCommand) : null;
        var source = new PegboardTerminalSource
        {
            TabId = tabId,
            TabTitle = NullIfBlank(tabTitle),
            Command = command,
            MaskedSecrets = maskedText + maskedCommand,
            WorkingDirectory = NullIfBlank(containing?.WorkingDirectory) ?? NullIfBlank(workingDirectory),
            ExitCode = containing?.ExitCode,
            CapturedUtc = containing?.FinishedUtc ?? nowUtc,
            HeadOmitted = clipped,
        };
        return new TerminalPegboardItem(text, null, source);
    }

    /// <summary>選択を含むコマンドの出力のうち一番新しいもの（そのタブで実行したものだけ）。見つからなければ null。
    /// 右クリックの瞬間に1回だけ走る。全文の正規化は高いので、選択の一番長い行で絞ってから確かめる。</summary>
    public static TerminalCommandOutput? FindContaining(
        IEnumerable<TerminalCommandOutput> outputs, Guid tabId, string selection)
    {
        var lines = NormalizedLines(selection).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0)
            return null;
        var probe = lines.MaxBy(l => l.Length)!;
        var needle = string.Join("\n", NormalizedLines(selection.Trim()));
        foreach (var output in outputs.Where(o => o.TabId == tabId).OrderByDescending(o => o.FinishedUtc))
        {
            if (!output.Text.Contains(probe, StringComparison.Ordinal))
                continue;
            if (lines.Count == 1 || Normalize(output.Text).Contains(needle, StringComparison.Ordinal))
                return output;
        }
        return null;
    }

    /// <summary>出典のタブで該当箇所を探す目印：本文の最初の空でない行の頭（前後の空白を除く）。無ければ null。</summary>
    public static string? Anchor(string content)
    {
        foreach (var line in NormalizedLines(content))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed.Length <= AnchorChars ? trimmed : trimmed[..AnchorChars];
        }
        return null;
    }

    /// <summary>出典の呼び名（カードの「出典: …」）。コマンドが分かればコマンド＋成否、続けてタブ名。</summary>
    public static string Label(PegboardTerminalSource source)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(source.Command))
        {
            var badge = RecentTerminalCommands.Badge(source.ExitCode);
            var command = RecentTerminalCommands.Title(source.Command);
            parts.Add(badge.Length == 0 ? command : $"{command} {badge}");
        }
        parts.Add($"ターミナル「{TabName(source)}」");
        return string.Join(" · ", parts);
    }

    /// <summary>出典行のツールチップ。残した時点の事実を全部並べる（カードの1行には収まらない分）。</summary>
    public static string ToolTip(PegboardTerminalSource source)
    {
        var lines = new List<string> { $"ターミナル: {TabName(source)}" };
        if (!string.IsNullOrWhiteSpace(source.Command))
            lines.Add($"コマンド: {RecentTerminalCommands.Title(source.Command)}");
        if (!string.IsNullOrWhiteSpace(source.WorkingDirectory))
            lines.Add($"場所: {source.WorkingDirectory}");
        if (source.ExitCode is { } exit)
            lines.Add($"終了コード: {exit}");
        var time = source.CapturedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        lines.Add(string.IsNullOrWhiteSpace(source.Command) ? $"残した時刻: {time}" : $"終了時刻: {time}");
        if (source.HeadOmitted)
            lines.Add("（先頭を省略）");
        if (source.MaskedSecrets > 0)
            lines.Add($"（秘密らしい文字列を {source.MaskedSecrets} か所伏せました）");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>エディタで開くときのタブ名（§24.21 の「出力-先頭語-時刻.log」に揃える）。</summary>
    public static string DocumentName(PegboardTerminalSource source)
    {
        var word = string.IsNullOrWhiteSpace(source.Command)
            ? "terminal"
            : RecentTerminalCommands.Title(source.Command).Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "terminal";
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safe = new string(word.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        var time = source.CapturedUtc.ToLocalTime().ToString("HHmmss", CultureInfo.InvariantCulture);
        return $"出力-{safe}-{time}.log";
    }

    /// <summary>上限を超えたら末尾を残す。行の途中からは始めない（§24.21 の <c>Clip</c> と同じ判断）。</summary>
    internal static (string Text, bool Clipped) Clip(string text)
    {
        if (text.Length <= MaxChars)
            return (text, false);
        var tail = text[^MaxChars..];
        var newline = tail.IndexOf('\n');
        if (newline >= 0 && newline < tail.Length - 1)
            tail = tail[(newline + 1)..];
        return (tail, true);
    }

    private static string TabName(PegboardTerminalSource source)
        => string.IsNullOrWhiteSpace(source.TabTitle) ? "Terminal" : source.TabTitle!;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>改行を揃え、行末の空白を落とす（ターミナルの選択と切り出しは行末の埋め草の扱いが違う）。</summary>
    private static IEnumerable<string> NormalizedLines(string text)
        => text.Replace("\r\n", "\n").Split('\n', '\r').Select(l => l.TrimEnd());

    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var line in NormalizedLines(text))
        {
            if (builder.Length > 0)
                builder.Append('\n');
            builder.Append(line);
        }
        return builder.ToString();
    }
}
