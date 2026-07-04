using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// NDJSON / JSON Lines（.ndjson / .jsonl）を 1 行 1 レコードの折りたたみツリーで表示する
/// <see cref="NdjsonEditorSupport"/> の検証。各行を 1 つの JSON 配列へまとめて既存の
/// <see cref="JsonTreeRenderer"/> の配列ツリーへ流す方針なので、件数・エラー行の握り・
/// 空行スキップ・HTML エスケープを確認する。
/// </summary>
public class NdjsonEditorSupportTests
{
    private static EditorSupportRegistry CreateRegistry()
    {
        return new(new IEditorSupportProvider[]
        {
            new JsonEditorSupport(new AiSettings(), new JsonSchemaValidator()),
            new NdjsonEditorSupport(new AiSettings())
        });
    }

    [Theory]
    [InlineData(@"C:\work\log.ndjson")]
    [InlineData(@"C:\work\records.jsonl")]
    [InlineData(@"C:\work\UPPER.NDJSON")]
    [InlineData(@"C:\work\UPPER.JSONL")]
    public void Resolve_NdjsonJsonlファイルにはNdjsonプロバイダを返す(string path)
    {
        var provider = CreateRegistry().Resolve(path);

        Assert.IsType<NdjsonEditorSupport>(provider);
    }

    [Fact]
    public void DescribeTitle_JSONLプレフィックスとファイル名()
    {
        var support = new NdjsonEditorSupport(new AiSettings());

        Assert.Equal("JSONL: log.ndjson", support.DescribeTitle(@"C:\work\log.ndjson"));
        Assert.IsAssignableFrom<IEditorSupportIncrementalHtmlProvider>(support);
    }

    [Fact]
    public void RenderHtml_各行を1レコードの配列ツリーにする()
    {
        var support = new NdjsonEditorSupport(new AiSettings());

        var html = support.RenderHtml(@"C:\work\log.ndjson", "{\"a\":1}\n{\"a\":2}\n{\"a\":3}");

        Assert.Contains("JSONL: log.ndjson", html);      // <title>
        Assert.Contains("id=\"json-root\"", html);
        Assert.Contains("3 要素", html);                 // ルート配列の件数
        Assert.Contains("\"a\"", html);                  // キー
        Assert.Contains("class=\"n\"", html);            // 数値の値
    }

    [Fact]
    public void RenderBody_壊れた行があっても例外を投げずその行だけエラー表示する()
    {
        var support = new NdjsonEditorSupport(new AiSettings());

        // 2 行目が壊れている。正しい 1・3 行目は描かれ、壊れ行はエラー代替に置き換わる。
        var body = support.RenderBody(@"C:\work\log.ndjson", "{\"a\":1}\n{bad\n{\"a\":3}");

        Assert.Contains("3 要素", body);                        // 壊れ行も代替オブジェクトとして 1 要素に数える
        Assert.Contains("__parse_error__", body);               // 壊れ行の目印
        Assert.Contains("2行目", body);                         // 何行目が壊れたか
        Assert.DoesNotContain("解析できません", body);          // 全体は壊れ扱いにしない
    }

    [Fact]
    public void RenderBody_空行は無視する()
    {
        var support = new NdjsonEditorSupport(new AiSettings());

        var body = support.RenderBody(@"C:\work\log.ndjson", "{\"a\":1}\n\n  \n{\"a\":2}\n");

        Assert.Contains("2 要素", body);   // 空行を除いた 2 レコードだけ
    }

    [Fact]
    public void RenderBody_値のHTML特殊文字をエスケープする()
    {
        var support = new NdjsonEditorSupport(new AiSettings());

        var body = support.RenderBody(@"C:\work\log.ndjson", "{\"html\":\"<b>&</b>\"}");

        Assert.Contains("&lt;b&gt;&amp;&lt;/b&gt;", body);
        Assert.DoesNotContain("<b>&</b>", body);
    }

    [Fact]
    public void RenderBody_空ファイルは空表示にする()
    {
        var support = new NdjsonEditorSupport(new AiSettings());

        var body = support.RenderBody(@"C:\work\log.ndjson", "");

        // [] を JsonTreeRenderer へ流すので空配列コンテナとして描かれる（例外なし）。
        Assert.DoesNotContain("解析できません", body);
        Assert.DoesNotContain("__parse_error__", body);
    }

    [Fact]
    public void NdjsonToJsonArray_各行の生テキストを保った配列にする()
    {
        var json = NdjsonEditorSupport.NdjsonToJsonArray("{\"a\":1}\n{\"a\":2}");

        // 各行をそのまま連結した 1 つの JSON 配列で、正しくパースできること。
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(2, doc.RootElement.GetArrayLength());
    }
}
