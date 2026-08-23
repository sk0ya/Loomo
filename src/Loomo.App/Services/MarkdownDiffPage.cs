using sk0ya.Loomo.Core.Diff;
using sk0ya.Loomo.Core.Markdown;

namespace sk0ya.Loomo.App.Services;

/// <summary>Markdown レンダリング差分の組み立て結果。<paramref name="Html"/> が null なら表示は諦めていて、
/// <paramref name="Notice"/> がその理由（利用者に見せる文言）。表示できるときの <paramref name="Notice"/> は空。
/// <paramref name="MapFolder"/> は相対パス画像の解決先（<c>preview.loomo</c> の実体）で、
/// <b>HTML と一組で運ぶ</b>——別々に置くと、割り込んだ古い読込が別ファイルの基準へ差し替えてしまう。</summary>
internal sealed record MarkdownDiffRender(string? Html, string Notice, string MapFolder = "");

/// <summary>
/// ブロック差分（<see cref="MarkdownBlockDiff"/>）を、Markdown プレビューと同じ土俵の HTML ページへ描く。
///
/// <para>本文は<b>新しい側</b>で、その流れの中に追加ブロックを ins、削除ブロックを del として混ぜる
/// （左右に2つのプレビューを並べない＝1本の読める文書にする）。ブロックごとに
/// <see cref="MarkdownRenderer.RenderToBody"/> へ渡すのがこの作りの要で、表・コードフェンス・リスト項目が
/// 単体で成り立つ単位に丸められている（ブロック丸めは <see cref="MarkdownBlockDiff"/> の仕事）から
/// 各ブロックを独立に変換しても構造が壊れない。</para>
///
/// <para>ページの器（テーマ CSS・mermaid・スクロール）は Markdown プレビューの
/// <see cref="MarkdownPage.BuildPage"/> をそのまま使う。差分の色だけを本文先頭の
/// <c>&lt;style&gt;</c> で足す——プレビュー側の CSS に差分の都合を混ぜないため。</para>
/// </summary>
internal static class MarkdownDiffPage
{
    /// <summary>レンダリング表示を諦める差分の行数。構文色（<see cref="DiffSyntaxHighlighter.MaxLines"/>）と
    /// <b>同じ頭打ちを参照する</b>（値を写すと片方だけ動いて静かにズレる）。全文コンテキストの差分は
    /// 1ファイル分の行を丸ごと持つため、ブロックへ丸めて HTML へ変換する手前で止める。</summary>
    internal const int MaxLines = DiffSyntaxHighlighter.MaxLines;

    private const string TooLargeNotice =
        "差分が大きすぎるため、レンダリング表示は省略しました（{0:N0} 行 > {1:N0} 行）。テキスト差分でご覧ください。";

    /// <summary>差分行からレンダリング差分のページを組み立てる（WPF に触れない＝バックグラウンドで呼べる）。</summary>
    /// <param name="lines">全行ぶんの差分（Gap を含まないこと）。</param>
    /// <param name="emptyNotice">差分そのものが無いときに見せる文言。</param>
    internal static MarkdownDiffRender Build(
        IReadOnlyList<DiffLine> lines, string title, string styleName, string? baseHref, string emptyNotice)
    {
        if (lines.Count == 0)
            return new MarkdownDiffRender(null, emptyNotice);
        if (lines.Count > MaxLines)
            return new MarkdownDiffRender(null, string.Format(TooLargeNotice, lines.Count, MaxLines));

        var blocks = MarkdownBlockDiff.Build(lines);
        if (blocks.Count == 0)
            return new MarkdownDiffRender(null, emptyNotice);

        return new MarkdownDiffRender(
            MarkdownPage.BuildPage(BuildBody(blocks), title, styleName, baseHref), "");
    }

    /// <summary>本文（差分用の CSS ＋ ブロックごとの変換結果）を組み立てる。</summary>
    internal static string BuildBody(IReadOnlyList<MarkdownDiffBlock> blocks)
    {
        var added = blocks.Count(b => b.Kind == MarkdownDiffBlockKind.Added);
        var removed = blocks.Count(b => b.Kind == MarkdownDiffBlockKind.Removed);
        var html = new System.Text.StringBuilder();
        html.Append("<style>").Append(DiffCss).AppendLine("</style>");
        html.Append("<div class=\"lmdiff-sum\">レンダリング差分　")
            .Append(added == 0 && removed == 0
                ? "変更されたブロックはありません"
                : $"＋追加 {added} ブロック　−削除 {removed} ブロック")
            .AppendLine("</div>");
        foreach (var block in blocks)
        {
            html.Append("<div class=\"lmdiff-b ").Append(ClassOf(block.Kind)).Append("\">");
            html.Append(MarkdownRenderer.RenderToBody(block.Text));
            html.AppendLine("</div>");
        }
        return html.ToString();
    }

    private static string ClassOf(MarkdownDiffBlockKind kind) => kind switch
    {
        MarkdownDiffBlockKind.Added => "lmdiff-ins",
        MarkdownDiffBlockKind.Removed => "lmdiff-del",
        _ => "lmdiff-same",
    };

    // 色は Diff ペイン本体（DiffSessionView.xaml.cs の AddedBg/AddedFg/RemovedBg/RemovedFg）と同じ値。
    // テーマ資源は WebView2 の中では引けないので、同じ緑・赤をここへ写して見え方を揃える
    // （片方だけ変えると同じペインの中で追加の緑が2色になる）。
    private const string DiffCss = """
        .lmdiff-sum { position: sticky; top: 0; z-index: 5; margin: 0 0 12px 0; padding: 4px 10px;
            font-size: 12px; opacity: .75; border-bottom: 1px solid rgba(128,128,128,.35);
            backdrop-filter: blur(2px); }
        .lmdiff-b { position: relative; padding: 1px 12px; border-left: 3px solid transparent; }
        .lmdiff-b > :first-child { margin-top: .25em; }
        .lmdiff-b > :last-child { margin-bottom: .25em; }
        .lmdiff-b ul, .lmdiff-b ol { margin-top: .25em; margin-bottom: .25em; }
        /* タスクリストのチェックボックスは押せなくする。プレビューでは押すとソースを書き換える仕掛けだが、
           ここは差分を読む場所で書き戻す先が無い（押せるのに何も起きない、どころか見た目だけ変わって
           「直した」ように見えてしまう）。 */
        .lmdiff-b input[type="checkbox"] { pointer-events: none; }
        .lmdiff-ins { background: rgba(76, 175, 80, .12); border-left-color: #81C784; }
        .lmdiff-del { background: rgba(229, 115, 115, .12); border-left-color: #E57373; opacity: .8; }
        .lmdiff-del, .lmdiff-del * { text-decoration: line-through; }
        .lmdiff-del img { filter: grayscale(1); }
        .lmdiff-ins::after, .lmdiff-del::after { position: absolute; right: 8px; top: 2px;
            font-size: 11px; font-weight: 600; letter-spacing: .05em; opacity: .8;
            text-decoration: none; pointer-events: none; }
        .lmdiff-ins::after { content: "＋追加"; color: #81C784; }
        .lmdiff-del::after { content: "−削除"; color: #E57373; }
        """;
}
