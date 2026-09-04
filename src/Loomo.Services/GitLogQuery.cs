using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace sk0ya.Loomo.Services;

/// <summary>
/// コミットログの照会条件。<b>絞り込みは git に渡す</b>のが要点——以前は読み込み済みの
/// <see cref="GitLogRow"/> をクライアント側で篩うだけだったので、ページ（既定200件）の外にある
/// 古いコミットは何を検索しても出てこなかった。ここで組み立てた引数を
/// <see cref="GitHistoryService.GetLogAsync(GitLogQuery)"/> が実行する。
///
/// <para><b>日付の意味のずれ</b>——一覧に出している日時は<b>作成日時</b>（<c>%ad</c>）だが、
/// git の <c>--since</c>/<c>--until</c> が見るのは<b>コミット日時</b>で、rebase を通ったコミットでは
/// 両者がずれる。そこで git へ渡す窓は前後1日ずつ広げ、<b>最終判定は表示している日時で</b>
/// クライアント側が行う（<see cref="CommitLogFilter"/>）。git 側は「そのページを引いてくるか」を
/// 決めるだけの粗い篩、という役割分担にしてある。</para>
///
/// <para><b>作者と本文は AND</b>——git は <c>--author</c> と <c>--grep</c> を両方満たすものだけ返す
/// （<c>--grep</c> 同士は OR なので、複数指定するときだけ <c>--all-match</c> を足す）。
/// 「どの項目でもいいから一致」という素の検索語は本文（件名）検索として渡す
/// （<see cref="CommitLogFilter.ToQueryHints"/> の判断）。</para>
/// </summary>
public sealed record GitLogQuery
{
    /// <summary>表示するリビジョン範囲（ブランチ名）。null なら <c>--all</c>。</summary>
    public string? BranchRef { get; init; }

    public int Limit { get; init; } = 300;

    public int Skip { get; init; }

    /// <summary>絞り込むパス（リポジトリルート基準・"/" 区切り）。</summary>
    public string? PathFilter { get; init; }

    /// <summary>リネームを追って履歴を続ける（<c>--follow</c>）。ファイル1件のときだけ意味がある。</summary>
    public bool FollowRenames { get; init; }

    /// <summary>現在のブランチの主系列だけを表示する。</summary>
    public bool FirstParent { get; init; }

    /// <summary>作者の絞り込み（部分一致・固定文字列）。複数は AND。</summary>
    public IReadOnlyList<string> Authors { get; init; } = Array.Empty<string>();

    /// <summary>本文（件名・本文）の絞り込み（部分一致・固定文字列）。複数は AND（<c>--all-match</c>）。</summary>
    public IReadOnlyList<string> Messages { get; init; } = Array.Empty<string>();

    /// <summary>この日以降（yyyy-MM-dd）。実際には1日広げて渡す（上のクラスコメント参照）。</summary>
    public DateOnly? Since { get; init; }

    /// <summary>この日まで（yyyy-MM-dd）。</summary>
    public DateOnly? Until { get; init; }

    /// <summary>git に渡す絞り込みが1つでもあるか（＝ページングが「絞り込み後の並び」になるか）。</summary>
    public bool HasFilters =>
        Authors.Count > 0 || Messages.Count > 0 || Since.HasValue || Until.HasValue;

    /// <summary>
    /// <c>git log</c> の引数を組み立てる（純粋・git は起動しない）。
    /// パスは必ず末尾の <c>--</c> の後ろへ置き、<c>--literal-pathspecs</c> でグロブ解釈を止める
    /// （<c>a[1].txt</c> の履歴に <c>a1.txt</c> が混ざらないように。<see cref="GitCompareArgs"/> と同じ扱い）。
    /// </summary>
    public string[] ToArguments()
    {
        var args = new List<string>
        {
            GitCompareArgs.LiteralPathspecs,
            "log",
            "--graph",
            string.IsNullOrWhiteSpace(BranchRef) ? "--all" : BranchRef,
            $"-n{Limit}",
        };
        if (Skip > 0)
            args.Add($"--skip={Skip}");
        args.Add("--date=format:%Y-%m-%d %H:%M");
        args.Add($"--pretty=format:{GitLogParser.PrettyFormat}");
        if (FirstParent)
            args.Add("--first-parent");

        // 検索語は正規表現ではなく固定文字列として扱う（"C++" や "a.b" がメタ文字として暴れないように）。
        if (Authors.Count > 0 || Messages.Count > 0)
        {
            args.Add("--fixed-strings");
            args.Add("--regexp-ignore-case");
        }
        foreach (var author in Authors)
            args.Add($"--author={author}");
        foreach (var message in Messages)
            args.Add($"--grep={message}");
        if (Messages.Count > 1)
            args.Add("--all-match");

        // 表示日時（作成日時）と git 側の基準（コミット日時）のずれを吸収するため前後1日広げる。
        // 端（0001-01-01／9999-12-31）は広げようがないのでそのまま使う——date:9999 のような
        // 入力は打てるので、ここで AddDays が例外を投げると一覧の更新ごと落ちる。
        if (Since is { } since)
            args.Add($"--since={Format(WidenBack(since))} 00:00:00");
        if (Until is { } until)
            args.Add($"--until={Format(WidenForward(until))} 23:59:59");

        if (FollowRenames && !string.IsNullOrWhiteSpace(PathFilter))
            args.Add("--follow");

        if (!string.IsNullOrWhiteSpace(PathFilter))
        {
            args.Add("--");
            args.Add(PathFilter);
        }
        return args.ToArray();
    }

    private static DateOnly WidenBack(DateOnly value) =>
        value > DateOnly.MinValue ? value.AddDays(-1) : value;

    private static DateOnly WidenForward(DateOnly value) =>
        value < DateOnly.MaxValue ? value.AddDays(1) : value;

    private static string Format(DateOnly value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
