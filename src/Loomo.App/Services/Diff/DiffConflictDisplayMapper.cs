using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Diff;

namespace sk0ya.Loomo.App.Services;

public sealed record DiffConflictDisplay(
    IReadOnlyList<object> Blocks, string OursHeader, string TheirsHeader, int Remaining);

/// <summary>解析済みコンフリクトを3ペイン用の表示ブロックへ変換する。</summary>
public sealed class DiffConflictDisplayMapper
{
    public DiffConflictDisplay Map(ParsedConflictFile parsed)
    {
        var blocks = new List<object>();
        var oursNo = 1;
        var resultNo = 1;
        var theirsNo = 1;
        foreach (var (region, index) in parsed.Regions.Select((region, index) => (region, index)))
        {
            if (region.Kind == ConflictRegionKind.Ordinary)
            {
                if (region.Lines.Count == 0) continue;
                var lines = Enumerable.Range(0, region.Lines.Count)
                    .Select(offset => new ConflictOrdinaryLineVm(
                        oursNo + offset, resultNo + offset, theirsNo + offset, region.Lines[offset]))
                    .ToList();
                blocks.Add(new ConflictOrdinaryBlockVm(lines));
                oursNo += region.Lines.Count;
                resultNo += region.Lines.Count;
                theirsNo += region.Lines.Count;
            }
            else
            {
                blocks.Add(new ConflictRegionVm(index, region.OursLabel ?? "Ours", region.TheirsLabel ?? "Theirs",
                    region.OursLines, region.TheirsLines, oursNo, resultNo, theirsNo));
                oursNo += region.OursLines.Count;
                theirsNo += region.TheirsLines.Count;
            }
        }

        var first = parsed.Regions.FirstOrDefault(region => region.Kind == ConflictRegionKind.Conflict);
        return new DiffConflictDisplay(blocks,
            first is null ? "" : $"Ours（{first.OursLabel ?? "HEAD"}）",
            first is null ? "" : $"Theirs（{first.TheirsLabel ?? "theirs"}）",
            parsed.Regions.Count(region => region.Kind == ConflictRegionKind.Conflict));
    }
}
