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

    /// <summary>候補キーを順に試し、最初に見つかった非空の string 値を返す。
    /// 小モデルが command を cmd/script 等の別名で送るキー揺れを吸収する。</summary>
    public static string GetStringAny(this JsonElement args, params string[] names)
    {
        foreach (var name in names)
        {
            var v = args.GetString(name);
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return "";
    }

    /// <summary>object に string プロパティがちょうど1つだけならその値を返す（複数・無しは空）。
    /// 想定外のキー名でも本文を拾う最後の砦。2つ以上は誤採用を避けて空を返す。</summary>
    public static string SingleStringValue(this JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return "";
        string? found = null;
        foreach (var p in args.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String) continue;
            if (found is not null) return "";   // 曖昧（複数）なら採用しない
            found = p.Value.GetString();
        }
        return found ?? "";
    }
}
