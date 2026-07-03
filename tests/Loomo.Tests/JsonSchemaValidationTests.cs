using System;
using System.IO;
using System.Linq;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// JsonSchemaValidator：$schema / 兄弟 .schema.json でスキーマを解決し、本文を検証する。
/// および JsonEditorSupport のツリーへの反映（違反バナー・波線・準拠バッジ）。
/// ネットワーク取得（http(s) の $schema）は外部依存になるためここでは扱わない。
/// </summary>
public class JsonSchemaValidationTests
{
    private const string Schema = """
        {
          "type": "object",
          "required": ["name"],
          "properties": {
            "name": { "type": "string" },
            "age":  { "type": "integer" }
          }
        }
        """;

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "loomo-jsonschema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void 兄弟のschemaファイルで検証し違反のパスを返す()
    {
        var dir = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "data.schema.json"), Schema);
            var jsonPath = Path.Combine(dir, "data.json");
            var validator = new JsonSchemaValidator();

            var ok = validator.Validate(jsonPath, """{ "name": "x", "age": 3 }""");
            Assert.True(ok.SchemaFound);
            Assert.False(ok.HasErrors);

            var bad = validator.Validate(jsonPath, """{ "age": "nope" }""");
            Assert.True(bad.SchemaFound);
            Assert.True(bad.HasErrors);
            Assert.Contains(bad.Errors, e => e.Path == "$.age");  // 型違反はその値の位置に付く
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 相対パスの_schemaを対象ファイル基準で解決する()
    {
        var dir = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "my.schema.json"), Schema);
            var validator = new JsonSchemaValidator();

            var result = validator.Validate(
                Path.Combine(dir, "data.json"),
                """{ "$schema": "./my.schema.json", "age": 1 }""");

            Assert.True(result.SchemaFound);
            Assert.True(result.HasErrors);   // name 必須が欠落
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void schemaが解決できなければ検証しない()
    {
        var dir = NewDir();
        try
        {
            var result = new JsonSchemaValidator().Validate(Path.Combine(dir, "data.json"), """{ "x": 1 }""");

            Assert.False(result.SchemaFound);
            Assert.False(result.HasErrors);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 壊れたJSONは検証扱いにしない()
    {
        var result = new JsonSchemaValidator().Validate(@"C:\work\data.json", "{ broken");

        Assert.False(result.SchemaFound);
    }

    [Fact]
    public void RenderBody_違反があると警告バナーと該当ノードの波線を出す()
    {
        var dir = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "data.schema.json"), Schema);
            var support = new JsonEditorSupport(new AiSettings(), new JsonSchemaValidator());

            var body = support.RenderBody(Path.Combine(dir, "data.json"), """{ "age": "nope" }""");

            Assert.Contains("class=\"schema-errors\"", body);
            Assert.Contains("スキーマ違反", body);
            Assert.Contains("class=\"line invalid\"", body);   // age（型違反）のノードに印
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RenderBody_適合していれば準拠バッジを出す()
    {
        var dir = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "data.schema.json"), Schema);
            var support = new JsonEditorSupport(new AiSettings(), new JsonSchemaValidator());

            var body = support.RenderBody(Path.Combine(dir, "data.json"), """{ "name": "x", "age": 2 }""");

            Assert.Contains("class=\"schema-ok\"", body);
            Assert.Contains("スキーマに準拠", body);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RenderBody_schemaが無ければバッジを出さない()
    {
        var dir = NewDir();
        try
        {
            var support = new JsonEditorSupport(new AiSettings(), new JsonSchemaValidator());

            var body = support.RenderBody(Path.Combine(dir, "data.json"), """{ "name": "x" }""");

            Assert.DoesNotContain("schema-ok", body);
            Assert.DoesNotContain("schema-errors", body);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
