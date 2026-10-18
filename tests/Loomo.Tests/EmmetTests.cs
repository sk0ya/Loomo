using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>Emmet の略記の切り出しと展開（純粋）。キャレット位置は展開後の本文の中のオフセット。</summary>
public class EmmetTests
{
    private static string Html(string abbreviation, string? wrapped = null)
    {
        var expansion = Emmet.Expand(abbreviation, EmmetSyntax.Html, "", "  ", wrapped);
        Assert.NotNull(expansion);
        return expansion!.Text;
    }

    // ===== 展開 =====

    [Fact]
    public void 子と繰り返しと番号()
    {
        Assert.Equal("<ul>\n  <li class=\"item1\"></li>\n  <li class=\"item2\"></li>\n  <li class=\"item3\"></li>\n</ul>",
            Html("ul>li.item$*3"));
    }

    [Fact]
    public void 兄弟と上る()
    {
        Assert.Equal("<div>\n  <p></p>\n</div>\n<footer></footer>", Html("div>p^footer"));
        Assert.Equal("<h1></h1>\n<p></p>", Html("h1+p"));
    }

    [Fact]
    public void idとclassと属性と本文()
    {
        Assert.Equal("<div id=\"main\" class=\"a b\" data-x=\"1\" title=\"hello world\">text</div>",
            Html("div#main.a.b[data-x=1 title=\"hello world\"]{text}"));
    }

    [Fact]
    public void 暗黙のタグは親から決まる()
    {
        Assert.Equal("<div class=\"box\"></div>", Html(".box"));
        Assert.Equal("<ul>\n  <li class=\"x\"></li>\n</ul>", Html("ul>.x"));
        Assert.Equal("<table>\n  <tr class=\"row\">\n    <td class=\"cell\"></td>\n  </tr>\n</table>", Html("table>.row>.cell"));
        Assert.Equal("<em><span class=\"x\"></span></em>", Html("em>.x"));
    }

    [Fact]
    public void 既定属性と空要素()
    {
        Assert.Equal("<a href=\"\"></a>", Html("a"));
        Assert.Equal("<img src=\"\" alt=\"\">", Html("img"));
        Assert.Equal("<input type=\"checkbox\" name=\"\" id=\"\">", Html("input:checkbox"));
        Assert.Equal("<a href=\"mailto:\"></a>", Html("a:mail"));
    }

    [Fact]
    public void インラインの子が少なければ1行に並べる()
    {
        Assert.Equal("<p><a href=\"\"></a><span></span></p>", Html("p>a+span"));
        Assert.Equal("<p>\n  <span></span>\n  <span></span>\n  <span></span>\n</p>", Html("p>span*3"));
    }

    [Fact]
    public void まとまりと繰り返し()
    {
        Assert.Equal("<dl>\n  <dt></dt>\n  <dd></dd>\n  <dt></dt>\n  <dd></dd>\n</dl>", Html("dl>(dt+dd)*2"));
    }

    [Fact]
    public void 番号の桁埋め_逆順_開始番号()
    {
        Assert.Equal("<div>\n  <p id=\"p01\"></p>\n  <p id=\"p02\"></p>\n  <p id=\"p03\"></p>\n</div>", Html("div>p#p$$*3"));
        Assert.Contains("item3", Html("ul>li.item$@-*3").Split('\n')[1]);
        Assert.Contains("item5", Html("ul>li.item$@5*2").Split('\n')[1]);
    }

    [Fact]
    public void キャレットは最初の空の属性値か中身()
    {
        var link = Emmet.Expand("a", EmmetSyntax.Html, "", "  ")!;
        Assert.Equal("<a href=\"".Length, link.CaretOffset);
        var div = Emmet.Expand("div.x", EmmetSyntax.Html, "", "  ")!;
        Assert.Equal("<div class=\"x\">".Length, div.CaretOffset);
    }

    [Fact]
    public void 二行目以降に行の字下げを付ける()
    {
        var expansion = Emmet.Expand("ul>li", EmmetSyntax.Html, "    ", "  ")!;
        Assert.Equal("<ul>\n      <li></li>\n    </ul>", expansion.Text);
    }

    [Fact]
    public void JSXはclassNameと閉じた空要素()
    {
        var expansion = Emmet.Expand("label.a[for=x]+br", EmmetSyntax.Jsx, "", "  ")!;
        Assert.Equal("<label htmlFor=\"x\" className=\"a\"></label>\n<br />", expansion.Text);
    }

    [Fact]
    public void HTML5の雛形()
    {
        var expansion = Emmet.Expand("!", EmmetSyntax.Html, "", "  ")!;
        Assert.StartsWith("<!DOCTYPE html>\n<html lang=\"en\">", expansion.Text);
        Assert.Equal(expansion.Text.IndexOf("</body>") - "\n".Length, expansion.CaretOffset);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("ul>")]
    [InlineData("div[")]
    [InlineData("a{x")]
    [InlineData(">p")]
    public void ただの語や壊れた略記は展開しない(string abbreviation) =>
        Assert.Null(Emmet.Expand(abbreviation, EmmetSyntax.Html, "", "  "));

    // ===== 囲む =====

    [Fact]
    public void 選択を一番内側の要素の中身にする()
    {
        Assert.Equal("<div class=\"note\">\n  <p>hello</p>\n</div>", Html("div.note>p", "hello"));
    }

    [Fact]
    public void 数の無い繰り返しは行ごとに繰り返す()
    {
        Assert.Equal("<ul>\n  <li>one</li>\n  <li>two</li>\n</ul>", Html("ul>li*", "one\n  two\n"));
    }

    [Fact]
    public void 複数行の選択は字下げを揃えて中へ入れる()
    {
        Assert.Equal("<section>\n  <p>a</p>\n    <p>b</p>\n</section>", Html("section", "  <p>a</p>\n    <p>b</p>"));
    }

    // ===== 切り出し =====

    [Theory]
    [InlineData("ul>li*3", 7, 0, "ul>li*3")]
    [InlineData("    div.box", 11, 4, "div.box")]
    [InlineData("<div>ul>li", 10, 5, "ul>li")]
    [InlineData("<div class=\"a>b\">p.x", 20, 17, "p.x")]
    [InlineData("text a[title=\"a b\"]", 20, 5, "a[title=\"a b\"]")]
    [InlineData("x (dt+dd)*2", 11, 2, "(dt+dd)*2")]
    public void キャレットの手前の略記を切り出す(string line, int caret, int start, string abbreviation)
    {
        var span = Emmet.Extract(line, caret);
        Assert.NotNull(span);
        Assert.Equal(start, span!.Start);
        Assert.Equal(abbreviation, span.Abbreviation);
    }

    [Theory]
    [InlineData("<div cla", 8)]
    [InlineData("abc ", 4)]
    [InlineData("", 0)]
    public void タグの中や空白の後では切り出さない(string line, int caret) =>
        Assert.Null(Emmet.Extract(line, caret));

    [Theory]
    [InlineData("a.html", EmmetSyntax.Html)]
    [InlineData("C:/x/App.vue", EmmetSyntax.Html)]
    [InlineData("Page.cshtml", EmmetSyntax.Html)]
    [InlineData("App.tsx", EmmetSyntax.Jsx)]
    public void 対象のファイル(string path, EmmetSyntax syntax) => Assert.Equal(syntax, Emmet.SyntaxFor(path));

    [Theory]
    [InlineData("a.cs")]
    [InlineData("a.ts")]
    [InlineData(null)]
    public void 対象外のファイル(string? path) => Assert.Null(Emmet.SyntaxFor(path));
}
