using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Services;

/// <summary>
/// <c>git log -g --date=iso-strict --format=</c><see cref="Format"/> の出力を読み、各記録を
/// 人が読む説明へ言い換える純ロジック。
///
/// <para>素の <c>git reflog</c> が使いにくいのは、<c>checkout: moving from 3f9a2c1d… to main</c>
/// のような git の内部語と40桁のハッシュで書かれているから。ここで「何をしたか」の種類
/// （<see cref="GitReflogKind"/>）と短い日本語の説明に直し、ハッシュは7桁、<c>refs/heads/</c> は落とす。
/// 読み分けられないメッセージは<b>そのまま</b>出す（推測で言い換えると嘘になる）。</para>
/// </summary>
public static partial class GitReflogParser
{
    /// <summary>レコードは RS（%x1e）で始め、項目は US（%x1f）で割る。並びは <see cref="Parse"/> と対。
    /// <c>%gd</c> は <c>--date</c> を付けると <c>HEAD@{2026-10-03T12:34:56+09:00}</c> になる＝記録の時刻
    /// （コミットの日時 <c>%cd</c> ではない。チェックアウトやリセットはコミットを作らないので、
    /// コミットの日時を出すと「いつ操作したか」が分からない）。</summary>
    public const string Format = "%x1e%H%x1f%h%x1f%gd%x1f%gs%x1f%s%x1f%an";

    /// <summary>
    /// 出力を読む。<paramref name="skip"/> は先頭の番号（ページの2枚目なら読み込み済みの件数）。
    /// <paramref name="take"/> を超えた1件は「次の記録」として、最後の行の <see cref="GitReflogEntry.PreviousHash"/>
    /// を埋めるためだけに使う（呼び出し側は <paramref name="take"/>+1 件を頼む）。
    /// </summary>
    public static GitReflogPage Parse(string output, string refName, int skip, int take)
    {
        var raw = new List<GitReflogEntry>();
        foreach (var record in output.Split('\x1e'))
        {
            var text = record.Trim('\r', '\n');
            if (text.Length == 0) continue;
            var fields = text.Split('\x1f');
            if (fields.Length < 6) continue;

            var (kind, description) = Classify(fields[3], fields[4]);
            raw.Add(new GitReflogEntry(
                refName,
                skip + raw.Count,
                fields[0].Trim(),
                fields[1].Trim(),
                ParseTime(fields[2]),
                kind,
                fields[3],
                description,
                fields[4],
                fields[5].Trim()));
        }

        var count = Math.Min(take, raw.Count);
        var entries = new List<GitReflogEntry>(count);
        for (var i = 0; i < count; i++)
            entries.Add(i + 1 < raw.Count ? raw[i] with { PreviousHash = raw[i + 1].Hash } : raw[i]);
        return new GitReflogPage(entries, raw.Count > take, null);
    }

    /// <summary><c>HEAD@{2026-10-03T12:34:56+09:00}</c> の括弧の中を時刻として読む。</summary>
    public static DateTimeOffset? ParseTime(string selector)
    {
        var open = selector.IndexOf("@{", StringComparison.Ordinal);
        var close = selector.LastIndexOf('}');
        if (open < 0 || close <= open + 2) return null;
        var value = selector[(open + 2)..close];
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : null;
    }

    [GeneratedRegex(@"^moving from (?<from>.+) to (?<to>.+)$")]
    private static partial Regex MovingFromTo();

    [GeneratedRegex(@"^\((?<step>[^)]*)\)")]
    private static partial Regex Parenthesized();

    /// <summary>
    /// メッセージ（<c>%gs</c>）を種類と説明へ。<paramref name="subject"/> はそのコミットの件名で、
    /// メッセージが件名を含まない記録（リセット・チェックアウト）では使わない。
    /// </summary>
    public static (GitReflogKind Kind, string Description) Classify(string message, string subject)
    {
        var text = message.Trim();
        var colon = text.IndexOf(": ", StringComparison.Ordinal);
        var action = colon >= 0 ? text[..colon] : text;
        var rest = colon >= 0 ? text[(colon + 2)..].Trim() : "";

        // スタッシュ（refs/stash の reflog）は動詞を持たず「WIP on main: …」「On main: …」で書かれる。
        if (action.StartsWith("WIP on ", StringComparison.Ordinal) || action.StartsWith("On ", StringComparison.Ordinal))
            return (GitReflogKind.Stash, $"{action[(action.IndexOf("on ", StringComparison.OrdinalIgnoreCase) + 3)..]} でスタッシュ: {rest}");

        // pull --rebase は「pull --rebase (pick): …」のように書かれる——中身はリベースなので、そちらで読む。
        if (action.StartsWith("pull --rebase", StringComparison.Ordinal) || action.StartsWith("pull -r ", StringComparison.Ordinal))
            return (GitReflogKind.Rebase, "プル（リベース）" + RebaseStep(StepOf(action), rest));

        var verb = action.Split(' ', 2)[0];
        switch (verb)
        {
            case "commit":
                var step = StepOf(action);
                return step switch
                {
                    "amend" => (GitReflogKind.Amend, $"コミットを修正: {rest}"),
                    "merge" => (GitReflogKind.Merge, $"マージをコミット: {rest}"),
                    "initial" => (GitReflogKind.Commit, $"最初のコミット: {rest}"),
                    _ => (GitReflogKind.Commit, rest.Length > 0 ? rest : subject),
                };
            case "checkout":
                var move = MovingFromTo().Match(rest);
                return move.Success
                    ? (GitReflogKind.Checkout, $"{ShortRef(move.Groups["from"].Value)} → {ShortRef(move.Groups["to"].Value)} に切り替え")
                    : (GitReflogKind.Checkout, rest.Length > 0 ? $"切り替え: {rest}" : "切り替え");
            case "reset":
                const string movingTo = "moving to ";
                return rest.StartsWith(movingTo, StringComparison.Ordinal)
                    ? (GitReflogKind.Reset, $"{ShortRef(rest[movingTo.Length..])} へリセット")
                    : (GitReflogKind.Reset, rest.Length > 0 ? $"リセット: {rest}" : "リセット");
            case "rebase":
                return (GitReflogKind.Rebase, "リベース" + RebaseStep(StepOf(action), rest));
            case "merge":
                var target = action.Length > "merge ".Length ? ShortRef(action["merge ".Length..]) : "";
                var mergedWhat = target.Length > 0 ? $"{target} を" : "";
                return rest.StartsWith("Fast-forward", StringComparison.Ordinal)
                    ? (GitReflogKind.Merge, $"{mergedWhat}マージ（早送り）")
                    : (GitReflogKind.Merge, $"{mergedWhat}マージ");
            case "pull":
                return rest.StartsWith("Fast-forward", StringComparison.Ordinal)
                    ? (GitReflogKind.Pull, "プル（早送り）")
                    : (GitReflogKind.Pull, rest.Length > 0 ? $"プル（マージ）: {rest}" : "プル");
            case "cherry-pick":
                return (GitReflogKind.CherryPick, $"チェリーピック: {(rest.Length > 0 ? rest : subject)}");
            case "revert":
                return (GitReflogKind.Revert, $"リバート: {(rest.Length > 0 ? rest : subject)}");
            case "branch":
            case "Branch":
                return (GitReflogKind.Branch, BranchStep(rest));
            case "clone":
                return (GitReflogKind.Clone, rest.Length > 0 ? $"クローン: {rest}" : "クローン");
            case "fetch":
                return (GitReflogKind.Sync, "フェッチ");
            case "update":
                if (action == "update by push") return (GitReflogKind.Sync, "プッシュ");
                break;
            case "am":
                return (GitReflogKind.Commit, $"パッチを適用: {(rest.Length > 0 ? rest : subject)}");
        }
        return (GitReflogKind.Other, text.Length > 0 ? text : subject);
    }

    /// <summary><c>rebase (pick)</c> の括弧の中（<c>pick</c>）。無ければ空。</summary>
    private static string StepOf(string action)
    {
        var open = action.IndexOf('(');
        if (open < 0) return "";
        var match = Parenthesized().Match(action[open..]);
        return match.Success ? match.Groups["step"].Value.Trim() : "";
    }

    private static string RebaseStep(string step, string rest) => step switch
    {
        // rebase (start): checkout main ＝ main の上に載せ替え始めた
        "start" => rest.StartsWith("checkout ", StringComparison.Ordinal)
            ? $"開始（{ShortRef(rest["checkout ".Length..])} の上へ）"
            : "開始",
        // rebase (finish): returning to refs/heads/feature
        "finish" => rest.StartsWith("returning to ", StringComparison.Ordinal)
            ? $"完了（{ShortRef(rest["returning to ".Length..])} へ戻る）"
            : "完了",
        "abort" => "を中止",
        "pick" => $"で再適用: {rest}",
        "reword" => $"でメッセージ変更: {rest}",
        "edit" => $"で編集: {rest}",
        "squash" => $"でまとめる: {rest}",
        "fixup" => $"でまとめる（fixup）: {rest}",
        "continue" => $"を続行: {rest}",
        "" => rest.Length > 0 ? $": {rest}" : "",
        _ => $"（{step}）: {rest}",
    };

    private static string BranchStep(string rest)
    {
        const string created = "Created from ";
        const string resetTo = "Reset to ";
        const string renamed = "renamed ";
        if (rest.StartsWith(created, StringComparison.Ordinal))
            return $"{ShortRef(rest[created.Length..])} からブランチを作成";
        if (rest.StartsWith(resetTo, StringComparison.Ordinal))
            return $"{ShortRef(rest[resetTo.Length..])} へブランチを付け替え";
        if (rest.StartsWith(renamed, StringComparison.Ordinal))
        {
            var names = rest[renamed.Length..].Split(" to ", 2);
            return names.Length == 2
                ? $"{ShortRef(names[0])} → {ShortRef(names[1])} に名前を変更"
                : $"名前を変更: {rest}";
        }
        return rest.Length > 0 ? $"ブランチ: {rest}" : "ブランチ";
    }

    [GeneratedRegex("^[0-9a-fA-F]{40}([0-9a-fA-F]{24})?$")]
    private static partial Regex FullHash();

    /// <summary>40桁（SHA-256 なら64桁）のハッシュは7桁に、<c>refs/heads/</c>・<c>refs/remotes/</c>・
    /// <c>refs/tags/</c> は落として読める名前にする。</summary>
    public static string ShortRef(string value)
    {
        var text = value.Trim();
        if (FullHash().IsMatch(text)) return text[..7];
        foreach (var prefix in new[] { "refs/heads/", "refs/remotes/", "refs/tags/" })
            if (text.StartsWith(prefix, StringComparison.Ordinal))
                return text[prefix.Length..];
        return text;
    }
}
