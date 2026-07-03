using System;
using System.IO;
using System.Text;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// PortableHtml：EditorSupport のプレビュー HTML を「単体で開ける」形へ変換する純ロジック
/// （エクスポート用）。仮想ホスト依存（base href / mermaid・marp スクリプト）の外し方を検証する。
/// </summary>
public class PortableHtmlTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "loomo-portable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void base_hrefをソースフォルダのfileURIへ書き換える()
    {
        var srcDir = NewDir();
        var assets = NewDir();
        try
        {
            var html = "<head><base href=\"https://preview.loomo/docs/\"></head>"
                     + "<body><img src=\"images/a.png\"></body>";

            var outp = PortableHtml.Build(html, srcDir, assets);

            Assert.DoesNotContain("preview.loomo", outp);
            Assert.Contains(new Uri(srcDir + Path.DirectorySeparatorChar).AbsoluteUri, outp);
            Assert.Contains("images/a.png", outp); // 相対 src はそのまま
        }
        finally
        {
            Directory.Delete(srcDir, true);
            Directory.Delete(assets, true);
        }
    }

    [Fact]
    public void mermaid参照があるときだけスクリプトをdataURIへ差し替える()
    {
        var assets = NewDir();
        try
        {
            const string stub = "console.log('MERMAID_STUB')";
            File.WriteAllText(Path.Combine(assets, "mermaid.min.js"), stub);
            const string url = "https://assets.loomo/mermaid.min.js";
            var html = "<body><pre class=\"mermaid\">graph</pre><script>var s='" + url + "'</script></body>";

            var outp = PortableHtml.Build(html, null, assets);

            Assert.DoesNotContain(url, outp);
            Assert.Contains("data:text/javascript;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(stub)), outp);
        }
        finally
        {
            Directory.Delete(assets, true);
        }
    }

    [Fact]
    public void mermaid参照が無ければURLはそのまま_巨大JSを埋め込まない()
    {
        var assets = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(assets, "mermaid.min.js"), "STUB");
            const string url = "https://assets.loomo/mermaid.min.js";
            var html = "<body><script>var s='" + url + "'</script></body>"; // .mermaid 要素なし

            var outp = PortableHtml.Build(html, null, assets);

            Assert.Contains(url, outp);
            Assert.DoesNotContain("data:text/javascript", outp);
        }
        finally
        {
            Directory.Delete(assets, true);
        }
    }

    [Fact]
    public void base_tagが無いHTMLは素通しする()
    {
        var assets = NewDir();
        try
        {
            const string html = "<body><div id=\"json-root\">x</div></body>";

            Assert.Equal(html, PortableHtml.Build(html, null, assets));
        }
        finally
        {
            Directory.Delete(assets, true);
        }
    }
}
