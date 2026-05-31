using System.Text.Json.Nodes;

namespace sk0ya.Loomo.Core.Tools;

/// <summary>
/// AIに渡すツール定義（名前・説明・入力JSON Schema）。
/// 各プロバイダ実装が自身のフォーマットへ変換する。
/// </summary>
public sealed record ToolDefinition(string Name, string Description, JsonObject InputSchema)
{
    /// <summary>単純な object スキーマを組み立てるヘルパ。</summary>
    public static JsonObject ObjectSchema(params (string name, string type, string description, bool required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, type, description, isRequired) in props)
        {
            properties[name] = new JsonObject
            {
                ["type"] = type,
                ["description"] = description
            };
            if (isRequired) required.Add(name);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required
        };
    }
}

/// <summary>ツール実行結果。</summary>
public sealed record ToolResult(string Content, bool IsError = false)
{
    public static ToolResult Ok(string content) => new(content, false);
    public static ToolResult Error(string message) => new(message, true);
}
