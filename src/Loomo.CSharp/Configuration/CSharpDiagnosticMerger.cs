using Editor.Core.Lsp;

namespace sk0ya.Loomo.CSharp.Configuration;

/// <summary>LSPとC#固有のcompiler／StyleCopフォールバック診断を、位置とIDで統合する。</summary>
/// <remarks>
/// LSPが一部の診断だけを返す場合にfallback全体を捨てず、同じ診断だけを除外する。
/// 表示側が診断の発生源や重複判定を再実装しないよう、C# DLLで共通化する。
/// </remarks>
public static class CSharpDiagnosticMerger
{
    /// <summary>Roslynが隣接する不要usingを一つのIDE0005範囲で返した場合に、
    /// コンパイラが個別に確認したCS8019の位置へ展開する。IDE0005の広い範囲だけから
    /// 不要なusingを推測すると、必要なusingまで警告してしまう。</summary>
    public static IReadOnlyList<LspDiagnostic> ExpandUnnecessaryUsingGroups(
        IReadOnlyList<LspDiagnostic> diagnostics, IReadOnlyList<LspRange> individualRanges)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(individualRanges);

        var expanded = new List<LspDiagnostic>(diagnostics.Count);
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Code is not { } code ||
                !(code.Equals("IDE0005", StringComparison.OrdinalIgnoreCase) ||
                  code.Equals("CS8019", StringComparison.OrdinalIgnoreCase)))
            {
                expanded.Add(diagnostic);
                continue;
            }

            var matches = individualRanges
                .Where(range => IsWithin(range, diagnostic.Range))
                .Distinct()
                .ToArray();
            if (matches.Length == 0)
            {
                expanded.Add(diagnostic);
                continue;
            }

            foreach (var range in matches)
                expanded.Add(diagnostic with { Range = range });
        }

        return expanded;
    }

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

    private static bool IsWithin(LspRange candidate, LspRange container)
        => Compare(candidate.Start, container.Start) >= 0 &&
           Compare(candidate.End, container.End) <= 0;

    private static int Compare(LspPosition left, LspPosition right)
        => left.Line != right.Line
            ? left.Line.CompareTo(right.Line)
            : left.Character.CompareTo(right.Character);
}
