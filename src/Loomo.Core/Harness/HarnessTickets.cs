using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Harness;

/// <summary>
/// 指示の中のチケット参照1件。<see cref="Key"/> が同一性（Azure DevOps は <c>AB#1234</c>、GitHub は
/// <c>owner/repo#12</c>）、<see cref="Url"/> は指示に URL で書かれていたときだけ持つ。
/// </summary>
public sealed record HarnessTicketRef(string Key, string? Url)
{
    /// <summary>Azure DevOps の Work Item 番号（<c>AB#</c> のとき）。それ以外は null。</summary>
    public int? WorkItemId => Key.StartsWith(HarnessTickets.AzureBoardsPrefix, StringComparison.OrdinalIgnoreCase)
        && int.TryParse(Key.AsSpan(HarnessTickets.AzureBoardsPrefix.Length), out var id) ? id : null;
}

/// <summary>
/// チケット（Azure DevOps の Work Item・GitHub の Issue）をタスクとして扱うための参照の読み取り（§37.3.2）。
/// <para>§37.3.1 と同じく<b>書かれているものだけ</b>を拾う。素の <c>#1234</c> は PR・見出し・Issue のどれか決まらないので
/// 拾わない。ブランチ名の番号も命名の約束ごとでしかないので拾わない（推測で結ばない）。</para>
/// </summary>
public static class HarnessTickets
{
    public const string AzureBoardsPrefix = "AB#";

    // AB#1234（Azure Boards の言及の書き方）。前が英数字なら別の語の一部（xAB#1）。日本語が直前に付く
    // 「対応AB#1」は拾いたいので \w（日本語も含む）ではなく ASCII だけで見る。後ろも同じ理由で数字だけ見る。
    private static readonly Regex AzureBoardsMention = new(@"(?<![A-Za-z0-9_#])AB#(?<id>\d+)(?![0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Work Item の画面 URL（dev.azure.com・*.visualstudio.com・Azure DevOps Server のどれも /_workitems/edit/<id>）。
    private static readonly Regex AzureDevOpsUrl = new(@"https?://[^\s<>()""'`]+?/_workitems/edit/(?<id>\d+)(?![0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex GitHubIssueUrl = new(@"https?://github\.com/(?<owner>[\w.-]+)/(?<repo>[\w.-]+)/issues/(?<id>\d+)(?![0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>文中のチケット参照（現れた順・<see cref="HarnessTicketRef.Key"/> で重複除去。URL のあるものを優先して残す）。</summary>
    public static IReadOnlyList<HarnessTicketRef> Find(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        var found = new List<(int At, HarnessTicketRef Ref)>();
        foreach (Match m in AzureDevOpsUrl.Matches(text))
            found.Add((m.Index, new HarnessTicketRef(WorkItemKey(m.Groups["id"].Value), m.Value)));
        foreach (Match m in GitHubIssueUrl.Matches(text))
            found.Add((m.Index, new HarnessTicketRef(
                IssueKey(m.Groups["owner"].Value, m.Groups["repo"].Value, m.Groups["id"].Value), m.Value)));
        foreach (Match m in AzureBoardsMention.Matches(text))
            found.Add((m.Index, new HarnessTicketRef(WorkItemKey(m.Groups["id"].Value), null)));

        var result = new List<HarnessTicketRef>();
        foreach (var (_, r) in found.OrderBy(f => f.At))
        {
            var index = result.FindIndex(x => SameKey(x.Key, r.Key));
            if (index < 0)
                result.Add(r);
            else if (result[index].Url is null && r.Url is not null)
                result[index] = r;
        }
        return result;
    }

    /// <summary>
    /// タスクファイルの <c>ticket:</c> の値を鍵にそろえる。<c>AB#1234</c>・URL のほか、番号だけ（<c>1234</c>）は
    /// Azure DevOps の Work Item とみなす（<c>ticket:</c> と書いた本人が番号の出どころを知っている）。読めなければ null。
    /// </summary>
    public static HarnessTicketRef? ParseDeclared(string? value)
    {
        var v = value?.Trim().Trim('"', '\'') ?? "";
        if (v.Length == 0)
            return null;
        if (v.All(char.IsAsciiDigit))
            return new HarnessTicketRef(WorkItemKey(v), null);
        // 画面に出す鍵（owner/repo#12）をそのまま写しても読めるように。
        if (DeclaredIssueKey.Match(v) is { Success: true } m)
            return new HarnessTicketRef(IssueKey(m.Groups["owner"].Value, m.Groups["repo"].Value, m.Groups["id"].Value), null);
        return Find(v).FirstOrDefault();
    }

    private static readonly Regex DeclaredIssueKey = new(@"^(?<owner>[\w.-]+)/(?<repo>[\w.-]+)#(?<id>\d+)$",
        RegexOptions.CultureInvariant);

    // 番号は数として揃える（AB#0012 と AB#12 を別のチケットにしない）。桁あふれは書かれたまま。
    private static string Number(string digits) => long.TryParse(digits, out var n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : digits;

    private static string WorkItemKey(string digits) => AzureBoardsPrefix + Number(digits);

    private static string IssueKey(string owner, string repo, string digits) => $"{owner}/{repo}#{Number(digits)}";

    public static bool SameKey(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
