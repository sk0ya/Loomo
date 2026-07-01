using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace sk0ya.Loomo.Core.Diff;

/// <summary>コンフリクトリージョンの種別。</summary>
public enum ConflictRegionKind
{
    Ordinary,
    Conflict,
}

/// <summary>コンフリクトリージョンの解決方法。</summary>
public enum ConflictResolution
{
    Ours,
    Theirs,
    Both,
}

/// <summary>
/// 作業ツリーの1リージョン。<see cref="ConflictRegionKind.Ordinary"/> は <see cref="Lines"/> のみ、
/// <see cref="ConflictRegionKind.Conflict"/> は <see cref="OursLines"/>/<see cref="TheirsLines"/>
/// （diff3 スタイルなら <see cref="BaseLines"/> も）が本文を持つ。
/// </summary>
/// <param name="StartLine">作業ツリーの生ファイル内での1始まり行番号（Ordinary は <see cref="Lines"/>[0]、
/// Conflict は <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> の次＝<see cref="OursLines"/>[0] の位置）。表示のガター用。</param>
public sealed record ConflictRegion(
    ConflictRegionKind Kind,
    IReadOnlyList<string> Lines,
    IReadOnlyList<string> OursLines,
    IReadOnlyList<string> TheirsLines,
    IReadOnlyList<string>? BaseLines,
    string? OursLabel,
    string? TheirsLabel,
    int StartLine);

/// <summary>解析結果。<see cref="EndsWithNewline"/> は再構築時に末尾改行の有無を保つために使う。</summary>
public sealed record ParsedConflictFile(IReadOnlyList<ConflictRegion> Regions, bool EndsWithNewline)
{
    public bool HasConflicts => Regions.Any(r => r.Kind == ConflictRegionKind.Conflict);
}

/// <summary>
/// 作業ツリーのファイルに残るコンフリクトマーカー（<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> /
/// 任意の <c>|||||||</c>（diff3 の共通祖先） / <c>=======</c> / <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c>）を
/// リージョン単位に分解する。UI 非依存・純粋関数（テスト可能）。<see cref="UnifiedPatchEditor"/> と対。
///
/// マーカー行はプレフィックスの一致だけで判定する（git 自体も同じ行ベースの判定で、本文中に偶然
/// 同じプレフィックスで始まる行があれば区別できないのは同じ制約）。構造が壊れている
/// （対応する <c>=======</c>/<c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c> が無い等）場合は安全側に倒し、
/// ファイル全体を1つの <see cref="ConflictRegionKind.Ordinary"/> リージョンとして返す。
/// </summary>
public static class ConflictMarkerParser
{
    private const string OursMarker = "<<<<<<<";
    private const string BaseMarker = "|||||||";
    private const string SeparatorMarker = "=======";
    private const string TheirsMarker = ">>>>>>>";

    public static ParsedConflictFile Parse(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        var endsWithNewline = normalized.Length > 0 && normalized[^1] == '\n';
        var rawLines = normalized.Split('\n');
        var lines = endsWithNewline && rawLines.Length > 0 && rawLines[^1].Length == 0
            ? rawLines[..^1]
            : rawLines;

        var regions = TryParseRegions(lines);
        return regions is not null
            ? new ParsedConflictFile(regions, endsWithNewline)
            : new ParsedConflictFile(new[] { OrdinaryRegion(lines, 1) }, endsWithNewline);
    }

    /// <summary>指定リージョン（コンフリクトのみ）を選んだ側で解決し、ファイル全体を再構築する。
    /// 他の未解決コンフリクトは元のマーカーごとそのまま残す。</summary>
    public static string ResolveRegion(ParsedConflictFile parsed, int regionIndex, ConflictResolution resolution) =>
        ResolveRegionCore(parsed, regionIndex, region => resolution switch
        {
            ConflictResolution.Ours => region.OursLines,
            ConflictResolution.Theirs => region.TheirsLines,
            ConflictResolution.Both => region.OursLines.Concat(region.TheirsLines),
            _ => throw new ArgumentOutOfRangeException(nameof(resolution)),
        });

    /// <summary>
    /// 指定リージョン（コンフリクトのみ）を、渡した行でそのまま置き換えて解決する（Rider 風 Result 欄の自由編集用。
    /// 空リストなら両側とも削除される）。他の未解決コンフリクトは元のマーカーごとそのまま残す。
    /// </summary>
    public static string ResolveRegionWithLines(
        ParsedConflictFile parsed, int regionIndex, IReadOnlyList<string> lines) =>
        ResolveRegionCore(parsed, regionIndex, _ => lines);

    /// <summary>ResolveRegion 系の共通の組み立て：対象リージョンだけ <paramref name="chooseLines"/> の結果に差し替え、
    /// 他は元のまま（Ordinary はそのまま、他のコンフリクトはマーカーごと）連結する。</summary>
    private static string ResolveRegionCore(
        ParsedConflictFile parsed, int regionIndex, Func<ConflictRegion, IEnumerable<string>> chooseLines)
    {
        if (regionIndex < 0 || regionIndex >= parsed.Regions.Count)
            throw new ArgumentOutOfRangeException(nameof(regionIndex));
        if (parsed.Regions[regionIndex].Kind != ConflictRegionKind.Conflict)
            throw new ArgumentException("指定したリージョンはコンフリクトではありません。", nameof(regionIndex));

        var sb = new StringBuilder();
        for (var i = 0; i < parsed.Regions.Count; i++)
        {
            var region = parsed.Regions[i];
            if (i == regionIndex)
            {
                foreach (var line in chooseLines(region)) sb.Append(line).Append('\n');
            }
            else if (region.Kind == ConflictRegionKind.Ordinary)
            {
                foreach (var line in region.Lines) sb.Append(line).Append('\n');
            }
            else
            {
                AppendConflictMarkers(sb, region);
            }
        }

        var result = sb.ToString();
        if (!parsed.EndsWithNewline && result.EndsWith('\n'))
            result = result[..^1];
        return result;
    }

    private static List<ConflictRegion>? TryParseRegions(string[] lines)
    {
        var regions = new List<ConflictRegion>();
        var ordinary = new List<string>();
        var ordinaryStart = 1;
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (!IsMarker(line, OursMarker))
            {
                if (ordinary.Count == 0)
                    ordinaryStart = i + 1;
                ordinary.Add(line);
                i++;
                continue;
            }

            if (ordinary.Count > 0)
            {
                regions.Add(OrdinaryRegion(ordinary, ordinaryStart));
                ordinary = new List<string>();
            }
            var oursLabel = ExtractLabel(line, OursMarker);
            i++;
            var oursStart = i + 1;

            var ours = new List<string>();
            while (i < lines.Length && !IsMarker(lines[i], BaseMarker) && !IsMarker(lines[i], SeparatorMarker))
            {
                ours.Add(lines[i]);
                i++;
            }
            if (i >= lines.Length) return null;

            List<string>? baseLines = null;
            if (IsMarker(lines[i], BaseMarker))
            {
                i++;
                baseLines = new List<string>();
                while (i < lines.Length && !IsMarker(lines[i], SeparatorMarker))
                {
                    baseLines.Add(lines[i]);
                    i++;
                }
                if (i >= lines.Length) return null;
            }

            i++; // "=======" をスキップ
            var theirs = new List<string>();
            while (i < lines.Length && !IsMarker(lines[i], TheirsMarker))
            {
                theirs.Add(lines[i]);
                i++;
            }
            if (i >= lines.Length) return null;

            var theirsLabel = ExtractLabel(lines[i], TheirsMarker);
            i++;

            regions.Add(new ConflictRegion(
                ConflictRegionKind.Conflict, Array.Empty<string>(), ours, theirs, baseLines,
                oursLabel, theirsLabel, oursStart));
        }
        if (ordinary.Count > 0)
            regions.Add(OrdinaryRegion(ordinary, ordinaryStart));
        return regions;
    }

    private static void AppendConflictMarkers(StringBuilder sb, ConflictRegion region)
    {
        sb.Append(OursMarker).Append(region.OursLabel is null ? "" : " " + region.OursLabel).Append('\n');
        foreach (var line in region.OursLines) sb.Append(line).Append('\n');
        if (region.BaseLines is not null)
        {
            sb.Append(BaseMarker).Append('\n');
            foreach (var line in region.BaseLines) sb.Append(line).Append('\n');
        }
        sb.Append(SeparatorMarker).Append('\n');
        foreach (var line in region.TheirsLines) sb.Append(line).Append('\n');
        sb.Append(TheirsMarker).Append(region.TheirsLabel is null ? "" : " " + region.TheirsLabel).Append('\n');
    }

    private static ConflictRegion OrdinaryRegion(IReadOnlyList<string> lines, int startLine) =>
        new(ConflictRegionKind.Ordinary, lines, Array.Empty<string>(), Array.Empty<string>(), null, null, null, startLine);

    private static bool IsMarker(string line, string prefix) => line.StartsWith(prefix, StringComparison.Ordinal);

    private static string? ExtractLabel(string line, string prefix)
    {
        if (line.Length <= prefix.Length) return null;
        var label = line[prefix.Length..].TrimStart(' ');
        return label.Length == 0 ? null : label;
    }
}
