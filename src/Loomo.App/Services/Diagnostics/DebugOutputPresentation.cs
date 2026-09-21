using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.App.Services;

internal readonly record struct DebugOutputStyle(string ForegroundResource, bool Emphasized);

/// <summary>デバッグ出力のカテゴリを、テーマリソースと強調表示属性へ写す。</summary>
internal static class DebugOutputPresentation
{
    internal static DebugOutputStyle For(DebugOutputCategory category) => category switch
    {
        DebugOutputCategory.Stderr => new("DebugStderr", false),
        DebugOutputCategory.Console => new("FgDim", false),
        DebugOutputCategory.Important => new("Accent", true),
        _ => new("Fg", false),
    };
}
