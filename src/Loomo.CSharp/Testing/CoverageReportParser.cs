using System.Globalization;
using System.Xml.Linq;

namespace sk0ya.Loomo.CSharp.Testing;

/// <summary>カバレッジレポートのファイル単位集計。</summary>
public sealed record CoverageLineSummary(int Line1, bool Covered);

public sealed record CoverageBranchSummary(int Line1, int CoveredBranches, int ValidBranches)
{
    public double Rate => ValidBranches == 0 ? 0 : (double)CoveredBranches / ValidBranches;
}

public sealed record CoverageFileSummary(
    string Path,
    int CoveredLines,
    int ValidLines,
    IReadOnlyList<CoverageLineSummary>? Lines = null,
    int CoveredBranches = 0,
    int ValidBranches = 0,
    IReadOnlyList<CoverageBranchSummary>? Branches = null)
{
    public IReadOnlyList<CoverageLineSummary> LineDetails => Lines ?? Array.Empty<CoverageLineSummary>();
    public IReadOnlyList<CoverageBranchSummary> BranchDetails => Branches ?? Array.Empty<CoverageBranchSummary>();
    public double LineRate => ValidLines == 0 ? 0 : (double)CoveredLines / ValidLines;
    public double BranchRate => ValidBranches == 0 ? 0 : (double)CoveredBranches / ValidBranches;
    public string LineSummary => $"行 {LineRate * 100:0.0}%（{CoveredLines}/{ValidLines}）";
    public string BranchSummary => ValidBranches == 0
        ? "分岐 —"
        : $"分岐 {BranchRate * 100:0.0}%（{CoveredBranches}/{ValidBranches}）";
}

/// <summary>Cobertura／OpenCover のカバレッジ集計。解析はC#機能DLLに閉じ込め、UIは表示だけを担当する。</summary>
public sealed record CoverageReport(
    string Format,
    int CoveredLines,
    int ValidLines,
    IReadOnlyList<CoverageFileSummary> Files,
    int CoveredBranches = 0,
    int ValidBranches = 0)
{
    public double LineRate => ValidLines == 0 ? 0 : (double)CoveredLines / ValidLines;
    public double BranchRate => ValidBranches == 0 ? 0 : (double)CoveredBranches / ValidBranches;
}

/// <summary>coverletが出力するCoberturaまたはOpenCover XMLを解析する。</summary>
public static class CoverageReportParser
{
    public static string? FindReport(string directory)
    {
        if (!Directory.Exists(directory)) return null;
        try
        {
            return Directory.EnumerateFiles(directory, "*.xml", SearchOption.AllDirectories)
                .Where(IsKnownReportName)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static CoverageReport? ParseFile(string path, out string? error)
    {
        error = null;
        try
        {
            return ParseXml(File.ReadAllText(path), out error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return null;
        }
    }

    public static CoverageReport? ParseXml(string xml, out string? error)
    {
        error = null;
        try
        {
            var root = XDocument.Parse(xml, LoadOptions.PreserveWhitespace).Root;
            if (root is null) { error = "XMLのルート要素がありません"; return null; }
            if (string.Equals(root.Name.LocalName, "coverage", StringComparison.OrdinalIgnoreCase))
                return ParseCobertura(root);
            if (string.Equals(root.Name.LocalName, "CoverageSession", StringComparison.OrdinalIgnoreCase))
                return ParseOpenCover(root);

            error = "対応していないカバレッジ形式です";
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Xml.XmlException)
        {
            error = ex.Message;
            return null;
        }
    }

    private static CoverageReport ParseCobertura(XElement root)
    {
        var files = new Dictionary<string, (int Covered, int Valid)>(StringComparer.OrdinalIgnoreCase);
        var lineDetails = new Dictionary<string, Dictionary<int, bool>>(StringComparer.OrdinalIgnoreCase);
        var branches = new Dictionary<string, (int Covered, int Valid)>(StringComparer.OrdinalIgnoreCase);
        var branchDetails = new Dictionary<string, Dictionary<int, (int Covered, int Valid)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var cls in root.Descendants().Where(e => e.Name.LocalName == "class"))
        {
            var path = (string?)cls.Attribute("filename");
            if (string.IsNullOrWhiteSpace(path)) continue;

            var covered = IntAttribute(cls, "lines-covered");
            var valid = IntAttribute(cls, "lines-valid");
            if (valid == 0)
            {
                var lines = cls.Descendants().Where(e => e.Name.LocalName == "line").ToArray();
                valid = lines.Length;
                covered = lines.Count(e => IntAttribute(e, "hits") > 0);
            }
            files.TryGetValue(path, out var old);
            files[path] = (old.Covered + covered, old.Valid + valid);
            var branchCovered = IntAttribute(cls, "branches-covered");
            var branchValid = IntAttribute(cls, "branches-valid");
            if (!lineDetails.TryGetValue(path, out var linesByNumber))
                lineDetails[path] = linesByNumber = new Dictionary<int, bool>();
            if (!branchDetails.TryGetValue(path, out var branchesByLine))
                branchDetails[path] = branchesByLine = new Dictionary<int, (int Covered, int Valid)>();
            foreach (var line in cls.Descendants().Where(e => e.Name.LocalName == "line"))
            {
                var number = IntAttribute(line, "number");
                if (number > 0)
                {
                    linesByNumber[number] = linesByNumber.GetValueOrDefault(number) || IntAttribute(line, "hits") > 0;
                    if (TryParseConditionCoverage((string?)line.Attribute("condition-coverage"), out var lineCovered, out var lineValid))
                    {
                        var previous = branchesByLine.GetValueOrDefault(number);
                        branchesByLine[number] = (previous.Covered + lineCovered, previous.Valid + lineValid);
                    }
                }
            }
            if (branchValid == 0) (branchCovered, branchValid) = branchesByLine.Values.Aggregate(
                (Covered: 0, Valid: 0), (sum, value) => (sum.Covered + value.Covered, sum.Valid + value.Valid));
            branches.TryGetValue(path, out var oldBranches);
            branches[path] = (oldBranches.Covered + branchCovered, oldBranches.Valid + branchValid);
        }

        var rootValid = IntAttribute(root, "lines-valid");
        var rootCovered = IntAttribute(root, "lines-covered");
        var summaries = files.Select(p =>
                branches.TryGetValue(p.Key, out var branch) ? new CoverageFileSummary(p.Key, p.Value.Covered, p.Value.Valid,
                    ToLineDetails(lineDetails, p.Key), branch.Covered, branch.Valid, ToBranchDetails(branchDetails, p.Key))
                : new CoverageFileSummary(p.Key, p.Value.Covered, p.Value.Valid, ToLineDetails(lineDetails, p.Key)))
            .OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (rootValid == 0 && summaries.Length > 0)
        {
            rootValid = summaries.Sum(p => p.ValidLines);
            rootCovered = summaries.Sum(p => p.CoveredLines);
        }
        var rootBranchValid = IntAttribute(root, "branches-valid");
        var rootBranchCovered = IntAttribute(root, "branches-covered");
        if (rootBranchValid == 0 && summaries.Length > 0)
        {
            rootBranchValid = summaries.Sum(p => p.ValidBranches);
            rootBranchCovered = summaries.Sum(p => p.CoveredBranches);
        }
        return new CoverageReport("Cobertura", rootCovered, rootValid, summaries, rootBranchCovered, rootBranchValid);
    }

    private static CoverageReport ParseOpenCover(XElement root)
    {
        var fileNames = root.Descendants().Where(e => e.Name.LocalName == "File")
            .Select(e => ((string?)e.Attribute("uid"), (string?)e.Attribute("fullPath")))
            .Where(p => p.Item1 is not null && !string.IsNullOrWhiteSpace(p.Item2))
            .ToDictionary(p => p.Item1!, p => p.Item2!, StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, (int Covered, int Valid)>(StringComparer.OrdinalIgnoreCase);
        var lineDetails = new Dictionary<string, Dictionary<int, bool>>(StringComparer.OrdinalIgnoreCase);
        var branchDetails = new Dictionary<string, Dictionary<int, (int Covered, int Valid)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var point in root.Descendants().Where(e => e.Name.LocalName == "SequencePoint"))
        {
            var id = (string?)point.Attribute("fileid");
            if (id is null || !fileNames.TryGetValue(id, out var path)) continue;
            files.TryGetValue(path, out var old);
            files[path] = (old.Covered + (IntAttribute(point, "vc") > 0 ? 1 : 0), old.Valid + 1);
            if (!lineDetails.TryGetValue(path, out var linesByNumber))
                lineDetails[path] = linesByNumber = new Dictionary<int, bool>();
            var line = IntAttribute(point, "sl");
            if (line > 0)
                linesByNumber[line] = linesByNumber.GetValueOrDefault(line) || IntAttribute(point, "vc") > 0;
        }

        foreach (var point in root.Descendants().Where(e => e.Name.LocalName == "BranchPoint"))
        {
            var id = (string?)point.Attribute("fileid");
            if (id is null || !fileNames.TryGetValue(id, out var path)) continue;
            if (!branchDetails.TryGetValue(path, out var byLine))
                branchDetails[path] = byLine = new Dictionary<int, (int Covered, int Valid)>();
            var line = IntAttribute(point, "sl");
            if (line <= 0) continue;
            var previous = byLine.GetValueOrDefault(line);
            byLine[line] = (previous.Covered + (IntAttribute(point, "vc") > 0 ? 1 : 0), previous.Valid + 1);
        }

        var summaries = files.Select(p => new CoverageFileSummary(p.Key, p.Value.Covered, p.Value.Valid,
                ToLineDetails(lineDetails, p.Key), BranchDetails(branchDetails, p.Key).Sum(b => b.CoveredBranches),
                BranchDetails(branchDetails, p.Key).Sum(b => b.ValidBranches), BranchDetails(branchDetails, p.Key)))
            .OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var summary = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "Summary");
        var valid = IntAttribute(summary, "numSequencePoints");
        var covered = IntAttribute(summary, "visitedSequencePoints");
        var branchValid = IntAttribute(summary, "numBranchPoints");
        var branchCovered = IntAttribute(summary, "visitedBranchPoints");
        if (valid == 0 && summaries.Length > 0)
        {
            valid = summaries.Sum(p => p.ValidLines);
            covered = summaries.Sum(p => p.CoveredLines);
        }
        if (branchValid == 0 && summaries.Length > 0)
        {
            branchValid = summaries.Sum(p => p.ValidBranches);
            branchCovered = summaries.Sum(p => p.CoveredBranches);
        }
        return new CoverageReport("OpenCover", covered, valid, summaries, branchCovered, branchValid);
    }

    private static bool IsKnownReportName(string path)
        => string.Equals(System.IO.Path.GetFileName(path), "coverage.cobertura.xml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(System.IO.Path.GetFileName(path), "coverage.opencover.xml", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<CoverageLineSummary> ToLineDetails(
        IReadOnlyDictionary<string, Dictionary<int, bool>> details, string path)
        => details.TryGetValue(path, out var lines)
            ? lines.OrderBy(p => p.Key).Select(p => new CoverageLineSummary(p.Key, p.Value)).ToArray()
            : Array.Empty<CoverageLineSummary>();

    private static IReadOnlyList<CoverageBranchSummary> ToBranchDetails(
        IReadOnlyDictionary<string, Dictionary<int, (int Covered, int Valid)>> details, string path)
        => details.TryGetValue(path, out var branches)
            ? BranchDetails(branches)
            : Array.Empty<CoverageBranchSummary>();

    private static IReadOnlyList<CoverageBranchSummary> BranchDetails(
        IReadOnlyDictionary<string, Dictionary<int, (int Covered, int Valid)>> details, string path)
        => details.TryGetValue(path, out var branches) ? BranchDetails(branches) : Array.Empty<CoverageBranchSummary>();

    private static IReadOnlyList<CoverageBranchSummary> BranchDetails(
        IReadOnlyDictionary<int, (int Covered, int Valid)> branches)
        => branches.OrderBy(p => p.Key)
            .Select(p => new CoverageBranchSummary(p.Key, p.Value.Covered, p.Value.Valid)).ToArray();

    private static bool TryParseConditionCoverage(string? value, out int covered, out int valid)
    {
        covered = valid = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var open = value.IndexOf('(');
        var slash = value.IndexOf('/', open + 1);
        var close = value.IndexOf(')', slash + 1);
        return open >= 0 && slash > open && close > slash
            && int.TryParse(value[(open + 1)..slash].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out covered)
            && int.TryParse(value[(slash + 1)..close].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out valid)
            && covered >= 0 && valid >= covered;
    }

    private static int IntAttribute(XElement? element, string name)
        => element is not null && int.TryParse((string?)element.Attribute(name), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value) ? Math.Max(0, value) : 0;
}
