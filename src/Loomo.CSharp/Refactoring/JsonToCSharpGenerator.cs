using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>JSONオブジェクトから、System.Text.Jsonで再利用できるC# POCO型を生成する。
/// 入力の意味解決は不要なため外部プロセスを起動せず、結果は呼び出し側でWorkspaceEditへ包む。</summary>
public sealed record JsonTypeGenerationResult(
    string? Text,
    string Summary,
    string? Error = null);

public static class JsonToCSharpGenerator
{
    public static JsonTypeGenerationResult Generate(
        string json, string rootTypeName = "Root", bool nullableEnabled = true)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Failed("JSONが空です。");

        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException ex) { return Failed($"JSONを解析できません: {ex.Message}"); }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Failed("ルートJSONはオブジェクトである必要があります。");

            var rootName = Identifier(rootTypeName, "Root");
            var context = new GenerationContext(nullableEnabled);
            context.AddObject(rootName, document.RootElement);
            var text = context.Render();
            return new JsonTypeGenerationResult(text, $"JSONから{rootName}型を生成");
        }
    }

    private static JsonTypeGenerationResult Failed(string error)
        => new(null, "", error);

    private sealed class GenerationContext(bool nullableEnabled)
    {
        private readonly List<ObjectType> _types = [];
        private readonly HashSet<string> _typeNames = new(StringComparer.Ordinal);

        public void AddObject(string name, JsonElement value)
        {
            if (!_typeNames.Add(name)) return;
            var type = new ObjectType(name);
            _types.Add(type);
            foreach (var property in value.EnumerateObject())
            {
                var propertyName = PropertyIdentifier(property.Name, type.Properties);
                var typeName = ValueType(property.Name, property.Value);
                type.Properties.Add(new Property(property.Name, propertyName, typeName));
            }
        }

        private string ValueType(string propertyName, JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    var objectName = Identifier(propertyName, "Value");
                    AddObject(objectName, value);
                    return ReferenceType(objectName);

                case JsonValueKind.Array:
                    return ArrayType(propertyName, value);

                case JsonValueKind.String:
                    return ReferenceType("string");

                case JsonValueKind.True:
                case JsonValueKind.False:
                    return "bool";

                case JsonValueKind.Number:
                    if (value.TryGetInt32(out _)) return "int";
                    if (value.TryGetInt64(out _)) return "long";
                    if (value.TryGetDecimal(out _)) return "decimal";
                    return "double";

                default:
                    return ReferenceType("object");
            }
        }

        private string ArrayType(string propertyName, JsonElement value)
        {
            var items = value.EnumerateArray().ToList();
            if (items.Count == 0)
                return $"global::System.Collections.Generic.List<{ReferenceType("object")}>";

            var first = items[0];
            string itemType;
            if (items.All(item => item.ValueKind == first.ValueKind))
            {
                itemType = first.ValueKind switch
                {
                    JsonValueKind.Object => ObjectArrayType(propertyName, items),
                    JsonValueKind.Number => NumberType(items),
                    _ => ValueType(propertyName, first).TrimEnd('?'),
                };
                if (nullableEnabled && items.Any(item => item.ValueKind == JsonValueKind.Null))
                    itemType += "?";
            }
            else
            {
                itemType = ReferenceType("object");
            }
            return $"global::System.Collections.Generic.List<{itemType}>";
        }

        private string ReferenceType(string typeName)
            => nullableEnabled ? typeName + "?" : typeName;

        private static string NumberType(IEnumerable<JsonElement> items)
        {
            var values = items.ToList();
            if (values.All(item => item.TryGetInt32(out _))) return "int";
            if (values.All(item => item.TryGetInt64(out _))) return "long";
            if (values.All(item => item.TryGetDecimal(out _))) return "decimal";
            return "double";
        }

        private string ObjectArrayType(string propertyName, IReadOnlyList<JsonElement> items)
        {
            var objectName = Identifier(Singularize(propertyName), "Item");
            AddObject(objectName, items[0]);
            return objectName;
        }

        public string Render()
        {
            var builder = new StringBuilder();
            foreach (var type in _types)
            {
                if (builder.Length > 0) builder.AppendLine().AppendLine();
                builder.Append("public sealed class ").Append(type.Name).AppendLine();
                builder.AppendLine("{");
                foreach (var property in type.Properties)
                {
                    if (!string.Equals(property.JsonName, property.Identifier, StringComparison.Ordinal))
                    {
                        builder.Append("    [global::System.Text.Json.Serialization.JsonPropertyName(\"")
                            .Append(Escape(property.JsonName)).AppendLine("\")]");
                    }
                    builder.Append("    public ").Append(property.TypeName).Append(' ')
                        .Append(property.Identifier).AppendLine(" { get; set; }");
                }
                builder.Append('}');
            }
            return builder.ToString();
        }

        private static string PropertyIdentifier(string jsonName, IReadOnlyList<Property> existing)
        {
            var identifier = Identifier(jsonName, "Value");
            var baseName = identifier;
            for (var suffix = 2; existing.Any(p => string.Equals(p.Identifier, identifier, StringComparison.Ordinal)); suffix++)
                identifier = baseName + suffix;
            return identifier;
        }

        private sealed class ObjectType(string name)
        {
            public string Name { get; } = name;
            public List<Property> Properties { get; } = [];
        }

        private sealed record Property(string JsonName, string Identifier, string TypeName);
    }

    private static string Identifier(string value, string fallback)
    {
        var words = value.Split([' ', '-', '_', '.', '/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder();
        foreach (var word in words)
        {
            var chars = word.Where(char.IsLetterOrDigit).ToArray();
            if (chars.Length == 0) continue;
            builder.Append(char.ToUpperInvariant(chars[0]));
            if (chars.Length > 1) builder.Append(chars, 1, chars.Length - 1);
        }
        if (builder.Length == 0) builder.Append(fallback);
        if (!SyntaxFacts.IsValidIdentifier(builder.ToString()))
            builder.Insert(0, "Model");
        var result = builder.ToString();
        return SyntaxFacts.IsKeyword(result) ? "@" + result : result;
    }

    private static string Singularize(string value)
    {
        var identifier = Identifier(value, "Item");
        if (identifier.EndsWith("ies", StringComparison.OrdinalIgnoreCase))
            return identifier[..^3] + "y";
        if (identifier.EndsWith("ses", StringComparison.OrdinalIgnoreCase))
            return identifier[..^2];
        if (identifier.EndsWith("s", StringComparison.OrdinalIgnoreCase) && identifier.Length > 1)
            return identifier[..^1];
        return identifier;
    }

    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static class SyntaxFacts
    {
        private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
            "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
            "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
            "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
            "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
            "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
            "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
            "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
            "void", "volatile", "while", "add", "alias", "ascending", "async", "await", "by",
            "descending", "dynamic", "equals", "from", "get", "global", "group", "into", "join",
            "let", "nameof", "not", "notnull", "on", "or", "orderby", "partial", "remove", "select",
            "set", "unmanaged", "value", "var", "when", "where", "with", "yield",
        };

        public static bool IsKeyword(string value) => Keywords.Contains(value);

        public static bool IsValidIdentifier(string value)
            => value.Length > 0 && (char.IsLetter(value[0]) || value[0] == '_')
               && value.Skip(1).All(c => char.IsLetterOrDigit(c) || c == '_');
    }
}
