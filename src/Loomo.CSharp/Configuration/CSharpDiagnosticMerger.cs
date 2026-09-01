using Editor.Core.Lsp;

namespace sk0ya.Loomo.CSharp.Configuration;

/// <summary>LSPとC#固有のcompiler／StyleCopフォールバック診断を、位置とIDで統合する。</summary>
/// <remarks>
/// LSPが一部の診断だけを返す場合にfallback全体を捨てず、同じ診断だけを除外する。
/// 表示側が診断の発生源や重複判定を再実装しないよう、C# DLLで共通化する。
/// </remarks>
public static class CSharpDiagnosticMerger
{
    /// <summary>primaryに同じ診断があるfallback項目だけを除外する。</summary>
    public static IReadOnlyList<LspDiagnostic> ExcludeDuplicates(
        IReadOnlyList<LspDiagnostic> primary,
        IEnumerable<LspDiagnostic> fallback)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(fallback);

        return fallback
            .Where(candidate => !primary.Any(existing => IsSame(existing, candidate)))
            .ToArray();
    }

    /// <summary>診断IDと範囲が一致する場合に同一診断とみなす。</summary>
    public static bool IsSame(LspDiagnostic left, LspDiagnostic right)
        => !string.IsNullOrWhiteSpace(left.Code) &&
           string.Equals(left.Code, right.Code, StringComparison.OrdinalIgnoreCase) &&
           left.Range.Start == right.Range.Start && left.Range.End == right.Range.End;
}
