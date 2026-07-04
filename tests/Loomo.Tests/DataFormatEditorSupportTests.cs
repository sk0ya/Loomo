using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// YAML/TOML の EditorSupport（YamlEditorSupport / TomlEditorSupport）の検証。
/// どちらもパース結果を JSON 化して JsonEditorSupport と同じ折りたたみツリーで表示するので、
/// 解決（拡張子→提供者）・主要な値の描画・壊れた入力の握り・タイトルを確認する。
/// JsonTreeRenderer の表記（"要素"/"項目"）は EditorSupportTests の JSON テストに合わせている。
/// </summary>
public class DataFormatEditorSupportTests
{
    private static EditorSupportRegistry CreateRegistry()
    {
        var settings = new AiSettings();
        var workspace = new FakeWorkspaceService();
        return new(new IEditorSupportProvider[]
        {
            new MarkdownEditorSupport(settings, workspace),
            new JsonEditorSupport(settings, new JsonSchemaValidator()),
            new YamlEditorSupport(settings),
            new TomlEditorSupport(settings),
        });
    }

    [Theory]
    [InlineData(@"C:\work\config.yaml")]
    [InlineData(@"C:\work\config.yml")]
    [InlineData(@"C:\work\UPPER.YAML")]
    public void Resolve_YamlファイルにはYamlプロバイダを返す(string path)
    {
        var provider = CreateRegistry().Resolve(path);

        Assert.IsType<YamlEditorSupport>(provider);
    }

    [Theory]
    [InlineData(@"C:\work\Cargo.toml")]
    [InlineData(@"C:\work\PROJECT.TOML")]
    public void Resolve_TomlファイルにはTomlプロバイダを返す(string path)
    {
        var provider = CreateRegistry().Resolve(path);

        Assert.IsType<TomlEditorSupport>(provider);
    }

    [Fact]
    public void YamlSupport_タイトルはYAMLプレフィックスとファイル名()
    {
        var support = new YamlEditorSupport(new AiSettings());

        Assert.Equal("YAML: config.yaml", support.DescribeTitle(@"C:\work\config.yaml"));
        Assert.IsAssignableFrom<IEditorSupportIncrementalHtmlProvider>(support);
    }

    [Fact]
    public void TomlSupport_タイトルはTOMLプレフィックスとファイル名()
    {
        var support = new TomlEditorSupport(new AiSettings());

        Assert.Equal("TOML: Cargo.toml", support.DescribeTitle(@"C:\work\Cargo.toml"));
        Assert.IsAssignableFrom<IEditorSupportIncrementalHtmlProvider>(support);
    }

    [Fact]
    public void YamlSupport_キーと値と配列件数を折りたたみツリーにする()
    {
        var support = new YamlEditorSupport(new AiSettings());
        const string yaml = "name: loomo\ncount: 3\nitems:\n  - a\n  - b\n";

        var html = support.RenderHtml(@"C:\work\config.yaml", yaml);
        Assert.Contains("YAML: config.yaml", html);   // <title>
        Assert.Contains("id=\"json-root\"", html);

        var body = support.RenderBody(@"C:\work\config.yaml", yaml);
        Assert.Contains("\"name\"", body);
        Assert.Contains("loomo", body);
        Assert.Contains("2 要素", body);   // items 配列は 2 要素
    }

    [Fact]
    public void TomlSupport_キーとテーブルと配列件数を折りたたみツリーにする()
    {
        var support = new TomlEditorSupport(new AiSettings());
        const string toml = "name = \"loomo\"\ntags = [\"a\", \"b\", \"c\"]\n[server]\nport = 8080\n";

        var html = support.RenderHtml(@"C:\work\Cargo.toml", toml);
        Assert.Contains("TOML: Cargo.toml", html);   // <title>
        Assert.Contains("id=\"json-root\"", html);

        var body = support.RenderBody(@"C:\work\Cargo.toml", toml);
        Assert.Contains("\"name\"", body);
        Assert.Contains("loomo", body);
        Assert.Contains("\"server\"", body);   // テーブルがネストされたオブジェクトになる
        Assert.Contains("8080", body);
        Assert.Contains("3 要素", body);       // tags 配列は 3 要素
    }

    [Fact]
    public void TomlSupport_数値と日時が正しいJSON値になる()
    {
        var support = new TomlEditorSupport(new AiSettings());
        const string toml = "port = 8080\nwhen = 2026-07-04T10:20:30Z\n";

        var body = support.RenderBody(@"C:\work\data.toml", toml);

        // 数値は数値リーフ（クォートされない）、日時は TomlDateTime.ToString() の綺麗な文字列になる。
        Assert.Contains("8080", body);
        Assert.Contains("2026-07-04T10:20:30Z", body);
        // 既定シリアライズだと漏れる内部構造が出ていないこと。
        Assert.DoesNotContain("SecondPrecision", body);
    }

    [Fact]
    public void YamlSupport_壊れた入力はエラー本文を出し例外を投げない()
    {
        var support = new YamlEditorSupport(new AiSettings());

        var body = support.RenderBody(@"C:\work\bad.yaml", "a: [1, 2\nb: :::");

        Assert.Contains("解析できません", body);
        Assert.Contains("class=\"raw\"", body);   // 原文を併記
    }

    [Fact]
    public void TomlSupport_壊れた入力はエラー本文を出し例外を投げない()
    {
        var support = new TomlEditorSupport(new AiSettings());

        var body = support.RenderBody(@"C:\work\bad.toml", "name = \nfoo = = bar");

        Assert.Contains("解析できません", body);
        Assert.Contains("class=\"raw\"", body);
    }

    [Fact]
    public void 空テキストは空表示になる()
    {
        Assert.Contains("空のファイル", new YamlEditorSupport(new AiSettings()).RenderBody(@"C:\work\config.yaml", ""));
        Assert.Contains("空のファイル", new TomlEditorSupport(new AiSettings()).RenderBody(@"C:\work\Cargo.toml", "   "));
    }

    [Fact]
    public void YamlSupport_HTML特殊文字をエスケープする()
    {
        var support = new YamlEditorSupport(new AiSettings());

        var body = support.RenderBody(@"C:\work\config.yaml", "html: \"<b>&</b>\"");

        Assert.Contains("&lt;b&gt;&amp;&lt;/b&gt;", body);
        Assert.DoesNotContain("<b>&</b>", body);
    }
}
