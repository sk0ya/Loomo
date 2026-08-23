using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using sk0ya.Loomo.Core.Diff;

namespace sk0ya.Loomo.Core.Markdown;

/// <summary>レンダリング差分の1ブロックの種別。新側を本文として読ませ、その流れの中に
/// 削除ブロック（旧側）を混ぜるので、片側にしか無いものは Added / Removed になる。</summary>
public enum MarkdownDiffBlockKind
{
    /// <summary>両側に同じ形で在るブロック（そのまま本文として描く）。</summary>
    Unchanged,
    /// <summary>新側にだけ在るブロック（ins として描く）。</summary>
    Added,
    /// <summary>旧側にだけ在るブロック（del として描く）。</summary>
    Removed,
}

/// <summary>レンダリング差分の1ブロック。<paramref name="Text"/> は Markdown の**素**（改行区切り）で、
/// 単体で Markdown として成り立つ単位（段落・リスト項目・コードフェンス・表…）になっている。</summary>
public sealed record MarkdownDiffBlock(MarkdownDiffBlockKind Kind, string Text);

/// <summary>
/// 行単位の差分を **Markdown のブロック単位へ丸める**（UI 非依存の純粋関数）。
///
/// <para>なぜ要るか: Markdown を行で切ると壊れる。表の途中で切れば表ではなくなり、コードフェンスの
/// 開きだけを取れば残り全部がコードになり、リスト項目の継続行だけを取れば宙に浮く。
/// レンダリングした上で差分を読ませるには、変更を**そのブロックまるごと**へ広げてから描くしかない。</para>
///
/// <para>作り: 差分行の並びから旧側（文脈＋削除）と新側（文脈＋追加）の2つの文書を復元し、
/// それぞれを Markdown のブロックへ区切る（<see cref="Segment"/>）。区切りは各文書の中だけで決まるので、
/// 差分の都合でフェンスや表が割れることがない。そのうえで、両側の**変更を含まない**ブロックのうち
/// 同じ差分行（同じ添字の範囲）を占めるものを錨にして突き合わせ、錨から錨までを1つの変更のかたまりとして
/// 「旧側＝Removed」「新側＝Added」の2ブロックに吐く。</para>
///
/// <para>リストは<b>項目ごと</b>に区切る（1項目直しただけでリスト全体が赤と緑になるのを避ける）。
/// ただし同じリストの中で**同じ種別が続くブロックは1つに束ねる**——項目ごとに描くと
/// <c>&lt;ul&gt;</c> が項目の数だけ生まれて箇条書きに見えなくなるため。順序付きリストでは、
/// 束ねたブロックの先頭項目の番号を**その項目の実際の序数**へ書き換える（分割した後続ブロックが
/// 1 から数え直さないように。レンダラは先頭の数字を <c>&lt;ol start&gt;</c> にする）。</para>
/// </summary>
public static class MarkdownBlockDiff
{
    /// <summary>Markdown とみなす拡張子。同じ一覧が App 側にもある（<c>MarkdownEditorSupport.Extensions</c>／
    /// <c>ShellWindow.SelectionActions.MarkdownExtensions</c>）。<b>Core にあるこれを今後の正本にする</b>
    /// ——UI にもファイルにも依らない判定なので、統合するならここへ寄せる。</summary>
    private static readonly string[] MarkdownExtensions = [".md", ".markdown"];

    /// <summary>このパスは Markdown か（レンダリング表示を出してよいか）。</summary>
    public static bool IsMarkdownPath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && MarkdownExtensions.Contains(
               System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>旧新の全文からブロック差分を組み立てる（アドホック比較の経路）。</summary>
    public static IReadOnlyList<MarkdownDiffBlock> Build(string oldText, string newText)
        => Build(DiffUtil.ComputeFull(oldText, newText));

    /// <summary>
    /// 全行ぶんの差分（<see cref="DiffUtil.ComputeFull"/> ／全文コンテキストの git パッチ）から
    /// ブロック差分を組み立てる。<see cref="DiffLineKind.Gap"/> は畳まれた文脈行の印なので、
    /// **渡してはいけない**（畳まれた行が抜けた文書は Markdown として別物になる）。
    /// </summary>
    public static IReadOnlyList<MarkdownDiffBlock> Build(IReadOnlyList<DiffLine> lines)
    {
        var oldSide = new List<SideLine>();
        var newSide = new List<SideLine>();
        for (var i = 0; i < lines.Count; i++)
        {
            switch (lines[i].Kind)
            {
                case DiffLineKind.Added:
                    newSide.Add(new SideLine(lines[i].Text, i, Changed: true));
                    break;
                case DiffLineKind.Removed:
                    oldSide.Add(new SideLine(lines[i].Text, i, Changed: true));
                    break;
                case DiffLineKind.Gap:
                    break; // 畳まれた文脈行（本来ここへは来ない）
                default:
                    oldSide.Add(new SideLine(lines[i].Text, i, Changed: false));
                    newSide.Add(new SideLine(lines[i].Text, i, Changed: false));
                    break;
            }
        }
        return Merge(Segment(oldSide), Segment(newSide));
    }

    /// <summary>片側の文書の1行（本文・元の差分行の添字・その側だけに在る行か）。</summary>
    private readonly record struct SideLine(string Text, int Index, bool Changed);

    /// <summary>片側の文書を区切った1ブロック。<c>First</c>/<c>Last</c> は元の差分行の添字で、
    /// 両側の錨合わせに使う。<c>Group</c> は同じリストに属する項目に振る通し番号（0＝リストではない）。</summary>
    private sealed record Seg(
        int First, int Last, bool Changed, int Group, bool Ordered, int Ordinal, List<string> Lines);

    // ===== 区切り =====

    private static readonly Regex FenceRe = new(@"^ {0,3}(`{3,}|~{3,})", RegexOptions.Compiled);
    private static readonly Regex HeadingRe = new(@"^ {0,3}#{1,6}(\s|$)", RegexOptions.Compiled);
    private static readonly Regex ThematicRe = new(@"^ {0,3}([-*_])[ \t]*(\1[ \t]*){2,}$", RegexOptions.Compiled);
    private static readonly Regex ListItemRe = new(@"^( {0,3})([-*+]|\d{1,9}[.)])(\s|$)", RegexOptions.Compiled);
    /// <summary>行頭の生 HTML タグ（開き・閉じ・コメント／宣言）。CommonMark の HTML ブロック相当。</summary>
    private static readonly Regex HtmlTagRe = new(@"^ {0,3}<(/?)([A-Za-z][A-Za-z0-9-]*)", RegexOptions.Compiled);

    /// <summary>ブロックとして扱う HTML タグ名（CommonMark の HTML ブロック（type 6）の一覧に倣う）。
    /// インラインの <c>&lt;b&gt;</c>／<c>&lt;code&gt;</c> や自動リンク <c>&lt;https://…&gt;</c> を
    /// ブロックの始まりと読まないための絞り込み。</summary>
    private static readonly HashSet<string> HtmlBlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "base", "blockquote", "body", "caption", "center", "col",
        "colgroup", "dd", "details", "dialog", "dir", "div", "dl", "dt", "fieldset", "figcaption",
        "figure", "footer", "form", "frame", "frameset", "h1", "h2", "h3", "h4", "h5", "h6", "head",
        "header", "hr", "html", "iframe", "legend", "li", "link", "main", "menu", "menuitem", "nav",
        "noframes", "ol", "optgroup", "option", "p", "param", "picture", "section", "source",
        "summary", "table", "tbody", "td", "tfoot", "th", "thead", "title", "tr", "track", "ul",
        "video", "audio",
    };

    private static bool IsBlank(string line) => line.Trim().Length == 0;

    /// <summary>その行が「新しいブロックを始める」行か（段落の続きとして飲み込めない行）。</summary>
    private static bool StartsNewBlock(string line)
        => FenceRe.IsMatch(line) || HeadingRe.IsMatch(line) || ThematicRe.IsMatch(line)
           || IsListItem(line) || IsHtmlBlockStart(line);

    /// <summary>行頭からブロックレベルの生 HTML が始まるか（<c>&lt;details&gt;</c> や
    /// <c>&lt;div align="center"&gt;</c> の類）。</summary>
    private static bool IsHtmlBlockStart(string line)
    {
        if (line.TrimStart().StartsWith("<!", StringComparison.Ordinal))
            return true;    // コメント・宣言
        var m = HtmlTagRe.Match(line);
        return m.Success && HtmlBlockTags.Contains(m.Groups[2].Value);
    }

    /// <summary>字下げの浅い（＝トップレベルの）リスト項目か。水平線 <c>---</c> は項目ではない。</summary>
    private static bool IsListItem(string line)
        => !ThematicRe.IsMatch(line) && ListItemRe.IsMatch(line);

    /// <summary>行頭の字下げ幅（タブは4桁として数える）。リスト項目の入れ子と兄弟を見分けるために使う。</summary>
    private static int IndentOf(string line)
    {
        var width = 0;
        foreach (var c in line)
        {
            if (c == ' ') width++;
            else if (c == '\t') width += 4;
            else break;
        }
        return width;
    }

    private static bool IsOrderedMarker(string line)
    {
        var m = ListItemRe.Match(line);
        return m.Success && char.IsDigit(m.Groups[2].Value[0]);
    }

    /// <summary>片側の文書を Markdown のブロックへ区切る。</summary>
    private static List<Seg> Segment(List<SideLine> lines)
    {
        var segs = new List<Seg>();
        var group = 0;
        var i = 0;
        while (i < lines.Count)
        {
            var text = lines[i].Text;

            if (IsBlank(text))
            {
                var start = i;
                while (i < lines.Count && IsBlank(lines[i].Text)) i++;
                segs.Add(MakeSeg(lines, start, i, 0, false, 0));
                continue;
            }

            if (FenceRe.Match(text) is { Success: true } fence)
            {
                var marker = fence.Groups[1].Value;
                var start = i;
                i++;
                while (i < lines.Count && !IsClosingFence(lines[i].Text, marker)) i++;
                if (i < lines.Count) i++;   // 閉じフェンスも同じブロックへ
                segs.Add(MakeSeg(lines, start, i, 0, false, 0));
                continue;
            }

            if (HeadingRe.IsMatch(text) || ThematicRe.IsMatch(text))
            {
                segs.Add(MakeSeg(lines, i, i + 1, 0, false, 0));
                i++;
                continue;
            }

            if (IsListItem(text))
            {
                ScanList(lines, ref i, ++group, segs);
                continue;
            }

            if (IsHtmlBlockStart(text))
            {
                var htmlStart = i;
                ScanHtmlBlock(lines, ref i);
                segs.Add(MakeSeg(lines, htmlStart, i, 0, false, 0));
                continue;
            }

            // 段落（表・引用・字下げコードもここ。空行か新しいブロックの始まりまでが1つ）
            var paraStart = i;
            i++;
            while (i < lines.Count && !IsBlank(lines[i].Text) && !StartsNewBlock(lines[i].Text)) i++;
            segs.Add(MakeSeg(lines, paraStart, i, 0, false, 0));
        }
        return segs;
    }

    /// <summary>
    /// ブロックレベルの生 HTML を1つのブロックとして飲み込む。<b>対応する閉じタグまで</b>——
    /// 見つからなければ CommonMark と同じく空行まで。
    ///
    /// <para>なぜ閉じタグまで見るか: ブロックごとに独立して HTML へ変換する（<c>MarkdownDiffPage</c>）ので、
    /// <c>&lt;details&gt;</c> … <c>&lt;/details&gt;</c> の間で切ると<b>開いたタグがそのブロックの中で閉じず</b>、
    /// 後続のブロックがまるごとその内側へ入る（README でよくある折りたたみの中に差分の大半が隠れる）。
    /// 同じタグの入れ子は数えて釣り合ったところで閉じる。</para>
    /// </summary>
    private static void ScanHtmlBlock(List<SideLine> lines, ref int i)
    {
        var m = HtmlTagRe.Match(lines[i].Text);
        var isOpening = m.Success && m.Groups[1].Value.Length == 0
                        && !lines[i].Text.TrimEnd().EndsWith("/>", StringComparison.Ordinal);
        if (isOpening)
        {
            var tag = m.Groups[2].Value;
            var depth = 0;
            for (var k = i; k < lines.Count; k++)
            {
                depth += CountTag(lines[k].Text, tag, closing: false)
                         - CountTag(lines[k].Text, tag, closing: true);
                if (depth <= 0)
                {
                    i = k + 1;
                    return;     // 釣り合った＝ここまでで1ブロック
                }
            }
        }
        // 閉じが見つからない／閉じタグ始まり／コメント：空行まで（CommonMark の HTML ブロックと同じ）。
        i++;
        while (i < lines.Count && !IsBlank(lines[i].Text)) i++;
    }

    /// <summary>1行の中の <c>&lt;tag</c>（または <c>&lt;/tag</c>）の数。タグ名の直後が区切りのものだけ数える
    /// （<c>&lt;div&gt;</c> を数えるときに <c>&lt;dialog&gt;</c> を巻き込まない）。</summary>
    private static int CountTag(string line, string tag, bool closing)
    {
        var needle = closing ? "</" + tag : "<" + tag;
        var count = 0;
        var at = 0;
        while ((at = line.IndexOf(needle, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var after = at + needle.Length;
            if (after >= line.Length || !char.IsLetterOrDigit(line[after]) && line[after] != '-')
                count++;
            at = after;
        }
        return count;
    }

    private static bool IsClosingFence(string line, string marker)
    {
        var trimmed = line.TrimStart(' ');
        return trimmed.StartsWith(marker, StringComparison.Ordinal)
               && trimmed.TrimEnd().All(c => c == marker[0]);
    }

    /// <summary>
    /// 1つのリストを項目ごとのブロックへ切る。項目には継続行（深く字下げされた入れ子・継続段落、
    /// 緩いリストの空行、字下げ無しの lazy continuation）を含める。<b>入れ子の項目を兄弟と読まない</b>のが
    /// 要点で、リストの字下げより深い行はすべてその項目の中身として扱う——さもないと
    /// 「入れ子の子項目だけが ins で、親項目は無変更」という宙に浮いたブロックが出る。
    /// </summary>
    private static void ScanList(List<SideLine> lines, ref int i, int group, List<Seg> segs)
    {
        var ordered = IsOrderedMarker(lines[i].Text);
        var listIndent = IndentOf(lines[i].Text);
        var ordinal = 0;
        while (i < lines.Count && IsSibling(lines[i].Text))
        {
            var start = i;
            ordinal++;
            i++;
            while (i < lines.Count)
            {
                var text = lines[i].Text;
                if (IsBlank(text))
                {
                    // 空行の次が項目より深く字下げされていれば、その項目の続き（緩いリスト・入れ子）。
                    var next = i;
                    while (next < lines.Count && IsBlank(lines[next].Text)) next++;
                    if (next < lines.Count && IndentOf(lines[next].Text) > listIndent)
                    {
                        i = next;
                        continue;
                    }
                    break;
                }
                if (IndentOf(text) > listIndent) { i++; continue; }   // 入れ子・継続段落
                if (IsListItem(text)) break;                          // 次の項目
                if (StartsNewBlock(text)) break;
                i++;   // 字下げ無しの継続行（lazy continuation）
            }
            var end = i;
            // 項目のあとの空行は、次も同じリストの項目ならこの項目へくっつける
            // （空行が独立したブロックになるとリストの束ね（同じ Group の連結）が切れる）。
            var after = i;
            while (after < lines.Count && IsBlank(lines[after].Text)) after++;
            if (after > i && after < lines.Count && IsSibling(lines[after].Text))
            {
                end = after;
                i = after;
            }
            segs.Add(MakeSeg(lines, start, end, group, ordered, ordinal));
        }

        // このリストの項目（同じ深さ・同じ番号付け）か。深いものは入れ子なので項目として数えない。
        bool IsSibling(string text)
            => IsListItem(text) && IndentOf(text) <= listIndent && IsOrderedMarker(text) == ordered;
    }

    private static Seg MakeSeg(List<SideLine> lines, int start, int end, int group, bool ordered, int ordinal)
    {
        var body = new List<string>(end - start);
        var changed = false;
        for (var i = start; i < end; i++)
        {
            body.Add(lines[i].Text);
            changed |= lines[i].Changed;
        }
        return new Seg(lines[start].Index, lines[end - 1].Index, changed, group, ordered, ordinal, body);
    }

    // ===== 突き合わせ =====

    /// <summary>旧側と新側のブロック列を、変更を含まない同じブロックを錨にして1本へ束ねる。</summary>
    private static List<MarkdownDiffBlock> Merge(List<Seg> old, List<Seg> now)
    {
        var pending = new List<(MarkdownDiffBlockKind Kind, Seg[] Segs)>();
        int i = 0, j = 0;
        while (i < old.Count || j < now.Count)
        {
            if (Aligned(old, i, now, j))
            {
                pending.Add((MarkdownDiffBlockKind.Unchanged, [now[j]]));
                i++;
                j++;
                continue;
            }
            var oldStart = i;
            var newStart = j;
            // 次に錨が合う位置まで、差分行の添字が小さい側から順に飲み込む。
            while ((i < old.Count || j < now.Count) && !Aligned(old, i, now, j))
            {
                if (i < old.Count && (j >= now.Count || old[i].First <= now[j].First)) i++;
                else j++;
            }
            if (i > oldStart)
                pending.Add((MarkdownDiffBlockKind.Removed, old.GetRange(oldStart, i - oldStart).ToArray()));
            if (j > newStart)
                pending.Add((MarkdownDiffBlockKind.Added, now.GetRange(newStart, j - newStart).ToArray()));
        }
        return Finish(pending);
    }

    /// <summary>両側のこのブロックが「同じ差分行を占める・変更を含まない」＝錨にできるか。</summary>
    private static bool Aligned(List<Seg> old, int i, List<Seg> now, int j)
        => i < old.Count && j < now.Count
           && !old[i].Changed && !now[j].Changed
           && old[i].First == now[j].First && old[i].Last == now[j].Last;

    /// <summary>同じリストの中で同じ種別が続くブロックを束ね、順序付きリストの番号を直し、
    /// 中身の無いブロックを落として最終形にする。</summary>
    private static List<MarkdownDiffBlock> Finish(List<(MarkdownDiffBlockKind Kind, Seg[] Segs)> pending)
    {
        var merged = new List<(MarkdownDiffBlockKind Kind, List<Seg> Segs)>();
        foreach (var (kind, segs) in pending)
        {
            if (segs.Length == 0) continue;
            var group = GroupOf(segs);
            if (merged.Count > 0 && merged[^1].Kind == kind && group != 0 && GroupOf(merged[^1].Segs) == group)
            {
                merged[^1].Segs.AddRange(segs);
                continue;
            }
            merged.Add((kind, segs.ToList()));
        }

        var blocks = new List<MarkdownDiffBlock>(merged.Count);
        foreach (var (kind, segs) in merged)
        {
            var lines = new List<string>();
            foreach (var seg in segs)
                lines.AddRange(seg.Lines);
            if (segs[0] is { Ordered: true, Ordinal: > 1 } head)
                lines[0] = Renumber(lines[0], head.Ordinal);
            var text = string.Join("\n", lines).TrimEnd('\n');
            if (text.Trim().Length == 0) continue;   // 空行だけのブロックは描くものが無い
            blocks.Add(new MarkdownDiffBlock(kind, text));
        }
        return blocks;
    }

    /// <summary>まとまりが属するリスト（全部同じリストの項目なら通し番号、そうでなければ 0）。</summary>
    private static int GroupOf(IReadOnlyList<Seg> segs)
    {
        var group = segs[0].Group;
        foreach (var seg in segs)
            if (seg.Group != group)
                return 0;
        return group;
    }

    /// <summary>順序付きリスト項目の番号を実際の序数へ書き換える（<c>&lt;ol start&gt;</c> の元になる）。</summary>
    private static string Renumber(string line, int ordinal)
    {
        var m = ListItemRe.Match(line);
        if (!m.Success) return line;
        var marker = m.Groups[2].Value;
        var delimiter = marker[^1];
        return m.Groups[1].Value + ordinal.ToString() + delimiter
               + line[(m.Groups[1].Value.Length + marker.Length)..];
    }
}
