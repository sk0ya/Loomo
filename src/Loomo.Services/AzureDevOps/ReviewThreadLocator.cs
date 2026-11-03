using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Services;

/// <summary>
/// PR のページで右クリックした所がどのスレッドかを決める（純粋）。ページの作り（DOM）には頼らず、右クリックした要素から
/// 外側へたどった<b>文字だけ</b>を受け取り、REST で読んだスレッドの本文と照らし合わせる——Azure DevOps の画面が作り変わっても
/// 「コメントの文字」は変わらないので壊れにくい。答え（ファイルと行）はいつも REST の側から取る。
/// </summary>
public static partial class ReviewThreadLocator
{
    /// <summary>照らし合わせに使うコメントの頭の長さ。短すぎるとほかのコメントにも当たる。</summary>
    public const int NeedleLength = 60;

    /// <param name="ancestorTexts">右クリックした要素から外側へ、各段の表示文字（内側が先）。</param>
    /// <param name="discussionId">ページの URL の <c>discussionId</c>（無ければ 0）。文字で決まらないときの手がかり。</param>
    public static AzureDevOpsThread? Find(IReadOnlyList<string> ancestorTexts, IReadOnlyList<AzureDevOpsThread> threads, int discussionId = 0)
    {
        var needles = threads
            .Select(t => (Thread: t, Needles: t.Comments.Select(c => Needle(c.Content)).Where(n => n.Length >= 6).ToList()))
            .Where(t => t.Needles.Count > 0)
            .ToList();
        foreach (var text in ancestorTexts)
        {
            var haystack = Normalize(text);
            if (haystack.Length == 0)
                continue;
            var hits = needles.Where(t => t.Needles.Any(n => haystack.Contains(n, StringComparison.Ordinal))).Select(t => t.Thread).ToList();
            if (hits.Count == 1)
                return hits[0];
            if (hits.Count > 1)
                break;   // 外側すぎて複数のスレッドを含んだ。ここから外はもっと増えるだけ
        }
        return discussionId > 0 ? threads.FirstOrDefault(t => t.Id == discussionId) : null;
    }

    private static string Needle(string markdown)
    {
        var text = Normalize(MarkdownToText(markdown));
        return text.Length > NeedleLength ? text[..NeedleLength] : text;
    }

    /// <summary>Markdown を表示される文字に近づける（画像は消す・リンクは文字だけ・記号を落とす）。</summary>
    public static string MarkdownToText(string markdown)
        => LinkPattern().Replace(ImagePattern().Replace(markdown, ""), "$1");

    /// <summary>比べる形：記号と空白をすべて落とし、小文字に（表示では <c>**ここ**は</c> が「ここは」になり、空白の入り方も変わるので）。</summary>
    public static string Normalize(string text)
        => SpacePattern().Replace(SymbolPattern().Replace(text, ""), "").ToLowerInvariant();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)|<img\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ImagePattern();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"[*_`#>~|\[\]]")]
    private static partial Regex SymbolPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacePattern();
}

/// <summary>
/// PR のコメントから手元のファイルを開くとき、どのワークツリーで開くか（純粋）。直すのは PR の元のブランチなので、
/// それをチェックアウトしているワークツリーが一番。次に Loomo がレビュー用に作った <c>pr/&lt;番号&gt;</c>。どちらも無ければ null
/// （黙って本体を開かず、作るか本体で開くかを人に選ばせる）。
/// </summary>
public static class ReviewWorktreeChooser
{
    public static GitWorktreeInfo? Choose(IReadOnlyList<GitWorktreeInfo> worktrees, string sourceBranch, int pullRequestId)
    {
        var live = worktrees.Where(w => !w.IsPrunable && !w.IsBare).ToList();
        return live.FirstOrDefault(w => string.Equals(w.Branch, sourceBranch, StringComparison.Ordinal))
               ?? live.FirstOrDefault(w => string.Equals(w.Branch, $"pr/{pullRequestId}", StringComparison.Ordinal));
    }
}
