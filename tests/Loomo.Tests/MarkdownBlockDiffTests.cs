using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Diff;
using sk0ya.Loomo.Core.Markdown;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// Markdown 差分のレンダリング表示（設計書 §24.10）の核＝**ブロック丸め**の単体テスト。
/// 行で切ると壊れるもの（コードフェンス・表・リスト・見出し）が、変更のたびに
/// 「そのブロックまるごと」の del/ins になることを確かめる。UI には一切触れない。
/// </summary>
public class MarkdownBlockDiffTests
{
    private static IReadOnlyList<MarkdownDiffBlock> Build(string oldText, string newText)
        => MarkdownBlockDiff.Build(oldText, newText);

    private static string Text(IReadOnlyList<MarkdownDiffBlock> blocks, MarkdownDiffBlockKind kind)
        => string.Join("\n---\n", blocks.Where(b => b.Kind == kind).Select(b => b.Text));

    // ===== Markdown 判定 =====

    [Theory]
    [InlineData("C:\\work\\README.md", true)]
    [InlineData("docs/設計/03-UIとレイアウト.MD", true)]
    [InlineData("notes.markdown", true)]
    [InlineData("Program.cs", false)]
    [InlineData("markdown", false)]      // 拡張子ではなくファイル名
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Markdown判定は拡張子で決まる(string? path, bool expected)
        => Assert.Equal(expected, MarkdownBlockDiff.IsMarkdownPath(path));

    // ===== 段落・見出し =====

    [Fact]
    public void 変更のない見出しはそのまま残り段落だけが差し替わる()
    {
        var blocks = Build("# 題\n\nこんにちは\n", "# 題\n\nこんばんは\n");

        Assert.Equal(
            new[] { MarkdownDiffBlockKind.Unchanged, MarkdownDiffBlockKind.Removed, MarkdownDiffBlockKind.Added },
            blocks.Select(b => b.Kind));
        Assert.Equal("# 題", blocks[0].Text);
        Assert.Equal("こんにちは", blocks[1].Text);
        Assert.Equal("こんばんは", blocks[2].Text);
    }

    [Fact]
    public void 段落の途中への追加は段落まるごとの差し替えになる()
    {
        var blocks = Build("いち\nさん\n", "いち\nに\nさん\n");

        Assert.Equal("いち\nさん", Text(blocks, MarkdownDiffBlockKind.Removed));
        Assert.Equal("いち\nに\nさん", Text(blocks, MarkdownDiffBlockKind.Added));
    }

    [Fact]
    public void 連続する追加行はひとつのブロックにまとまる()
    {
        var blocks = Build("A\n\nB\n", "A\n\n足す1\n足す2\n足す3\n\nB\n");

        // 追加は1ブロック（3行ぶんの ins が3つに割れない）
        Assert.Single(blocks, b => b.Kind == MarkdownDiffBlockKind.Added);
        Assert.Equal("足す1\n足す2\n足す3", Text(blocks, MarkdownDiffBlockKind.Added));
        Assert.Contains(blocks, b => b.Kind == MarkdownDiffBlockKind.Unchanged && b.Text == "A");
        Assert.Contains(blocks, b => b.Kind == MarkdownDiffBlockKind.Unchanged && b.Text == "B");
    }

    // ===== コードフェンス =====

    [Fact]
    public void コードフェンスの中の1行変更はフェンスごと差し替わる()
    {
        var before = "前置き\n\n```js\nlet a = 1;\nlet b = 2;\n```\n\n後書き\n";
        var after = "前置き\n\n```js\nlet a = 1;\nlet b = 9;\n```\n\n後書き\n";

        var blocks = Build(before, after);

        Assert.Equal("```js\nlet a = 1;\nlet b = 2;\n```", Text(blocks, MarkdownDiffBlockKind.Removed));
        Assert.Equal("```js\nlet a = 1;\nlet b = 9;\n```", Text(blocks, MarkdownDiffBlockKind.Added));
        // 前後の文は無傷
        Assert.Contains(blocks, b => b.Kind == MarkdownDiffBlockKind.Unchanged && b.Text == "前置き");
        Assert.Contains(blocks, b => b.Kind == MarkdownDiffBlockKind.Unchanged && b.Text == "後書き");
    }

    [Fact]
    public void フェンスの中の見出し風の行やリスト風の行では切られない()
    {
        var before = "```\n# これは見出しではない\n- これも項目ではない\n```\n";
        var after = "```\n# これは見出しではない\n- これも項目ではない\n+ 追記\n```\n";

        var blocks = Build(before, after);

        Assert.Equal(before.TrimEnd('\n'), Text(blocks, MarkdownDiffBlockKind.Removed));
        Assert.Equal(after.TrimEnd('\n'), Text(blocks, MarkdownDiffBlockKind.Added));
    }

    // ===== 表 =====

    [Fact]
    public void 表の1行変更は表ごと差し替わる()
    {
        var before = "| 名前 | 値 |\n|---|---|\n| a | 1 |\n| b | 2 |\n";
        var after = "| 名前 | 値 |\n|---|---|\n| a | 1 |\n| b | 9 |\n";

        var blocks = Build(before, after);

        Assert.Equal(before.TrimEnd('\n'), Text(blocks, MarkdownDiffBlockKind.Removed));
        Assert.Equal(after.TrimEnd('\n'), Text(blocks, MarkdownDiffBlockKind.Added));
    }

    // ===== リスト =====

    [Fact]
    public void リストは項目単位で切られ変わっていない項目は本文のまま残る()
    {
        var blocks = Build("- いち\n- に\n- さん\n", "- いち\n- ふたつ\n- さん\n");

        Assert.Equal("- に", Text(blocks, MarkdownDiffBlockKind.Removed));
        Assert.Equal("- ふたつ", Text(blocks, MarkdownDiffBlockKind.Added));
        Assert.Contains(blocks, b => b.Kind == MarkdownDiffBlockKind.Unchanged && b.Text == "- いち");
        Assert.Contains(blocks, b => b.Kind == MarkdownDiffBlockKind.Unchanged && b.Text == "- さん");
    }

    [Fact]
    public void 続けて変わらない項目はひとつの箇条書きに束ねられる()
    {
        // 末尾に1項目足しただけ：先頭3項目は1ブロック（項目ごとに <ul> が割れない）
        var blocks = Build("- a\n- b\n- c\n", "- a\n- b\n- c\n- d\n");

        Assert.Equal(
            new[] { MarkdownDiffBlockKind.Unchanged, MarkdownDiffBlockKind.Added },
            blocks.Select(b => b.Kind));
        Assert.Equal("- a\n- b\n- c", blocks[0].Text);
        Assert.Equal("- d", blocks[1].Text);
    }

    [Fact]
    public void 項目の継続行と入れ子は同じ項目に付いてくる()
    {
        var before = "- 親\n  - 子\n  続きの行\n- 次\n";
        var after = "- 親\n  - 子（変更）\n  続きの行\n- 次\n";

        var blocks = Build(before, after);

        Assert.Equal("- 親\n  - 子\n  続きの行", Text(blocks, MarkdownDiffBlockKind.Removed));
        Assert.Equal("- 親\n  - 子（変更）\n  続きの行", Text(blocks, MarkdownDiffBlockKind.Added));
        Assert.Contains(blocks, b => b.Kind == MarkdownDiffBlockKind.Unchanged && b.Text == "- 次");
    }

    [Fact]
    public void 順序付きリストは分割しても番号が1へ戻らない()
    {
        var blocks = Build("1. いち\n1. に\n1. さん\n", "1. いち\n1. ふたつ\n1. さん\n");

        // 先頭の番号がその項目の実際の序数になる（レンダラは <ol start=N> にする）
        Assert.Equal("2. に", Text(blocks, MarkdownDiffBlockKind.Removed));
        Assert.Equal("2. ふたつ", Text(blocks, MarkdownDiffBlockKind.Added));
        Assert.Contains(blocks, b => b.Kind == MarkdownDiffBlockKind.Unchanged && b.Text == "3. さん");
    }

    [Fact]
    public void 水平線はリスト項目とみなさない()
    {
        var blocks = Build("上\n\n---\n\n下\n", "上\n\n---\n\n下だった\n");

        Assert.Contains(blocks, b => b.Kind == MarkdownDiffBlockKind.Unchanged && b.Text == "---");
        Assert.Equal("下", Text(blocks, MarkdownDiffBlockKind.Removed));
        Assert.Equal("下だった", Text(blocks, MarkdownDiffBlockKind.Added));
    }

    // ===== 片側だけ・空 =====

    [Fact]
    public void 新規ファイルは全体が追加ブロックになる()
    {
        var blocks = Build("", "# 新しい\n\n本文\n");

        Assert.All(blocks, b => Assert.Equal(MarkdownDiffBlockKind.Added, b.Kind));
        Assert.Equal("# 新しい\n\n本文", string.Join("\n", blocks.Select(b => b.Text)));
    }

    [Fact]
    public void 削除されたファイルは全体が削除ブロックになる()
    {
        var blocks = Build("# 消える\n\n本文\n", "");

        Assert.All(blocks, b => Assert.Equal(MarkdownDiffBlockKind.Removed, b.Kind));
        Assert.Contains("# 消える", blocks[0].Text);
    }

    [Fact]
    public void 差分が無ければ全部そのままのブロックになる()
    {
        var blocks = Build("# 題\n\n本文\n", "# 題\n\n本文\n");

        Assert.All(blocks, b => Assert.Equal(MarkdownDiffBlockKind.Unchanged, b.Kind));
        Assert.Equal(new[] { "# 題", "本文" }, blocks.Select(b => b.Text));
    }

    [Fact]
    public void 空同士なら1つもブロックが出ない()
        => Assert.Empty(Build("", ""));

    [Fact]
    public void 空行だけのブロックは出さない()
    {
        var blocks = Build("A\n\n\nB\n", "A\n\nB\n");

        Assert.DoesNotContain(blocks, b => b.Text.Trim().Length == 0);
    }

    // ===== git パッチからの入口 =====

    [Fact]
    public void 全文パッチからでも同じブロック差分になる()
    {
        var patch = string.Join("\n",
            "diff --git a/README.md b/README.md",
            "index 1111111..2222222 100644",
            "--- a/README.md",
            "+++ b/README.md",
            "@@ -1,3 +1,3 @@",
            " # 題",
            " ",
            "-こんにちは",
            "+こんばんは");

        var blocks = MarkdownBlockDiff.Build(DiffUtil.FromUnifiedPatch(patch));

        Assert.Equal("# 題", blocks[0].Text);
        Assert.Equal("こんにちは", Text(blocks, MarkdownDiffBlockKind.Removed));
        Assert.Equal("こんばんは", Text(blocks, MarkdownDiffBlockKind.Added));
    }

    [Fact]
    public void 本文の水平線の削除がパッチから落ちない()
    {
        // `---` の削除行はパッチ上 `----`、追加行 `+++` は `++++` になる。綴りで判定すると
        // どちらも git のファイルヘッダと読まれて**行ごと消える**（水平線・フロントマターの囲み・
        // setext 見出しの下線が黙って落ちる）。
        var patch = string.Join("\n",
            "diff --git a/README.md b/README.md",
            "--- a/README.md",
            "+++ b/README.md",
            "@@ -1,3 +1,3 @@",
            " # 題",
            "-----",
            "+++++",
            " 本文");

        var lines = DiffUtil.FromUnifiedPatch(patch);

        Assert.Equal(
            new[] { "# 題", "----", "++++", "本文" },
            lines.Select(l => l.Text));
        Assert.Equal(DiffLineKind.Removed, lines[1].Kind);
        Assert.Equal(DiffLineKind.Added, lines[2].Kind);
    }

    [Fact]
    public void フロントマターの囲みを消した差分でも囲みが残る()
    {
        var patch = string.Join("\n",
            "--- a/doc.md",
            "+++ b/doc.md",
            "@@ -1,4 +1,1 @@",
            "----",
            "-title: 題",
            "----",
            " 本文");

        var blocks = MarkdownBlockDiff.Build(DiffUtil.FromUnifiedPatch(patch));

        // 囲みごと削除ブロックになる（キー行だけが地の文として残らない）
        Assert.Contains("---", Text(blocks, MarkdownDiffBlockKind.Removed));
        Assert.Contains("title: 題", Text(blocks, MarkdownDiffBlockKind.Removed));
        Assert.Contains(blocks, b => b.Kind == MarkdownDiffBlockKind.Unchanged && b.Text == "本文");
    }

    [Fact]
    public void ハンクの外の行は本文にしない()
    {
        // git が差分ではなくメッセージを返したとき（未追跡ファイルの読み取り失敗・fatal）。
        Assert.Empty(DiffUtil.FromUnifiedPatch("# 読み取り失敗: アクセスが拒否されました"));
        Assert.Empty(DiffUtil.FromUnifiedPatch("fatal: ambiguous argument"));
    }

    [Fact]
    public void 改行無しの注記はハンクを終わらせない()
    {
        var patch = string.Join("\n",
            "@@ -1,2 +1,2 @@",
            "-古い",
            "\\ No newline at end of file",
            "+新しい");

        var lines = DiffUtil.FromUnifiedPatch(patch);

        Assert.Equal(new[] { "古い", "新しい" }, lines.Select(l => l.Text));
        Assert.Equal(DiffLineKind.Added, lines[1].Kind);
    }

    // ===== ブロックレベルの生 HTML =====

    [Fact]
    public void 折りたたみは開きから閉じまでひとつのブロックになる()
    {
        var before = "<details>\n<summary>詳細</summary>\n\n本文\n\n</details>\n\nあとがき\n";
        var after = "<details>\n<summary>詳細</summary>\n\n本文（改）\n\n</details>\n\nあとがき\n";

        var blocks = Build(before, after);

        // <details> … </details> が割れない＝あとがきが折りたたみの内側へ入らない
        Assert.Equal(before.TrimEnd('\n').Replace("\n\nあとがき", ""),
            Text(blocks, MarkdownDiffBlockKind.Removed));
        Assert.Contains(blocks, b => b.Kind == MarkdownDiffBlockKind.Unchanged && b.Text == "あとがき");
        Assert.All(blocks, b => Assert.False(
            b.Text.Contains("<details>") && !b.Text.Contains("</details>")));
    }

    [Fact]
    public void 入れ子のdivも釣り合うまでひとつのブロックになる()
    {
        var before = "<div align=\"center\">\n  <div>中</div>\n</div>\n\n本文\n";
        var after = "<div align=\"center\">\n  <div>中身</div>\n</div>\n\n本文\n";

        var blocks = Build(before, after);

        Assert.Equal(before.TrimEnd('\n').Replace("\n\n本文", ""), Text(blocks, MarkdownDiffBlockKind.Removed));
        Assert.Contains(blocks, b => b.Kind == MarkdownDiffBlockKind.Unchanged && b.Text == "本文");
    }

    [Fact]
    public void 閉じないHTMLは空行までで切れる()
    {
        var blocks = Build("<div>ひらきっぱなし\n\n本文\n", "<div>ひらきっぱなし\n\n本文（改）\n");

        Assert.Contains(blocks, b => b.Kind == MarkdownDiffBlockKind.Unchanged && b.Text == "<div>ひらきっぱなし");
        Assert.Equal("本文", Text(blocks, MarkdownDiffBlockKind.Removed));
    }

    [Fact]
    public void インラインのタグはブロックの始まりにしない()
    {
        var blocks = Build("<b>強調</b>のある\n段落\n", "<b>強調</b>のある\n段落（改）\n");

        // 1つの段落として丸ごと差し替わる（<b> でブロックが切れない）
        Assert.Equal("<b>強調</b>のある\n段落", Text(blocks, MarkdownDiffBlockKind.Removed));
    }

    // ===== 閉じていないコードフェンス（既知の挙動） =====

    [Fact]
    public void 閉じないフェンスはその側の残りを飲み込む()
    {
        // プレビュー本体（MarkdownRenderer）と同じ扱い＝以降が全部コード。文書の残りが赤＋緑になるが、
        // 「描いたものと差分の切れ目が食い違う」よりは整合している。回帰に気づけるよう固定しておく。
        var blocks = Build("```\nコード\n", "```\nコード（改）\n");

        Assert.Equal("```\nコード", Text(blocks, MarkdownDiffBlockKind.Removed));
        Assert.Equal("```\nコード（改）", Text(blocks, MarkdownDiffBlockKind.Added));
        Assert.DoesNotContain(blocks, b => b.Kind == MarkdownDiffBlockKind.Unchanged);
    }

    // ===== ページ化（上限・理由） =====

    [Fact]
    public void 上限を超える差分はレンダリングせず理由を返す()
    {
        var lines = Enumerable.Range(0, MarkdownDiffPage.MaxLines + 1)
            .Select(i => new DiffLine(DiffLineKind.Context, $"行 {i}"))
            .ToList();

        var render = MarkdownDiffPage.Build(lines, "差分", "Dracula", null, "（差分はありません）");

        Assert.Null(render.Html);
        Assert.Contains("大きすぎる", render.Notice);
    }

    [Fact]
    public void 上限ちょうどならレンダリングする()
    {
        var lines = Enumerable.Range(0, MarkdownDiffPage.MaxLines)
            .Select(i => new DiffLine(DiffLineKind.Context, $"行 {i}"))
            .ToList();

        var render = MarkdownDiffPage.Build(lines, "差分", "Dracula", null, "（差分はありません）");

        Assert.NotNull(render.Html);
        Assert.Equal("", render.Notice);
    }

    [Fact]
    public void 差分が空ならその理由を返す()
    {
        var render = MarkdownDiffPage.Build([], "差分", "Dracula", null, "（差分はありません）");

        Assert.Null(render.Html);
        Assert.Equal("（差分はありません）", render.Notice);
    }

    [Fact]
    public void ページには追加と削除のブロックがクラス付きで並ぶ()
    {
        var blocks = Build("# 題\n\nこんにちは\n", "# 題\n\nこんばんは\n");

        var body = MarkdownDiffPage.BuildBody(blocks);

        Assert.Contains("lmdiff-del", body);
        Assert.Contains("lmdiff-ins", body);
        Assert.Contains("<h1", body);                 // 見出しは Markdown として描かれている
        Assert.Contains("こんばんは", body);
        Assert.Contains("＋追加 1 ブロック", body);
    }
}
