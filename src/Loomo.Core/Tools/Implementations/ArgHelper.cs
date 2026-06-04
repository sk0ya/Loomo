using System.Text.Json;

namespace sk0ya.Loomo.Core.Tools.Implementations;

/// <summary>ツール引数（JSON）から型安全に値を取り出す補助。</summary>
internal static class ArgHelper
{
    public static string GetString(this JsonElement args, string name, string fallback = "")
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;
}
