using sk0ya.Loomo.CSharp.Testing;

namespace sk0ya.Loomo.Tests;

public sealed class CoverageReportParserTests
{
    [Fact]
    public void Parses_coverlet_cobertura_and_aggregates_classes_with_the_same_file()
    {
        const string xml = """
            <coverage lines-covered="3" lines-valid="5">
              <packages><package name="P"><classes>
                <class filename="src/A.cs" lines-covered="2" lines-valid="3" />
                <class filename="src/A.cs"><methods><method><lines>
                  <line number="4" hits="1" /><line number="5" hits="0" />
                </lines></method></methods></class>
              </classes></package></packages>
            </coverage>
            """;

        var report = CoverageReportParser.ParseXml(xml, out var error);

        Assert.Null(error);
        Assert.NotNull(report);
        Assert.Equal("Cobertura", report!.Format);
        Assert.Equal(3, report.CoveredLines);
        Assert.Equal(5, report.ValidLines);
        var file = Assert.Single(report.Files);
        Assert.Equal("src/A.cs", file.Path);
        Assert.Equal(3, file.CoveredLines);
        Assert.Equal(5, file.ValidLines);
        Assert.Equal(new[] { new CoverageLineSummary(4, true), new CoverageLineSummary(5, false) }, file.LineDetails);
    }

    [Fact]
    public void Counts_branches_once_when_a_file_has_several_classes()
    {
        // coverlet は class に branches-valid を付けないので、分岐は行の condition-coverage から
        // 数える。1ファイルに複数クラス（入れ子型・partial の片割れ）があるとき、クラスごとに
        // 集計し直すと先のクラスの分岐を二重に数えてしまう。
        const string xml = """
            <coverage>
              <packages><package name="P"><classes>
                <class filename="src/A.cs"><methods><method><lines>
                  <line number="4" hits="1" condition-coverage="50% (1/2)" />
                </lines></method></methods></class>
                <class filename="src/A.cs"><methods><method><lines>
                  <line number="9" hits="1" condition-coverage="100% (2/2)" />
                </lines></method></methods></class>
              </classes></package></packages>
            </coverage>
            """;

        var report = CoverageReportParser.ParseXml(xml, out var error);

        Assert.Null(error);
        var file = Assert.Single(report!.Files);
        Assert.Equal(4, file.ValidBranches);
        Assert.Equal(3, file.CoveredBranches);
        Assert.Equal(4, report.ValidBranches);
        Assert.Equal(3, report.CoveredBranches);
    }

    [Fact]
    public void Parses_opencover_sequence_points()
    {
        const string xml = """
            <CoverageSession><Modules><Module><Files>
              <File uid="1" fullPath="C:\work\A.cs" />
            </Files><Classes><Class><Methods><Method><SequencePoints>
              <SequencePoint vc="2" fileid="1" sl="1" />
              <SequencePoint vc="0" fileid="1" sl="2" />
            </SequencePoints><BranchPoints>
              <BranchPoint vc="1" fileid="1" sl="1" />
              <BranchPoint vc="0" fileid="1" sl="1" />
            </BranchPoints></Method></Methods></Class></Classes></Module></Modules>
            <Summary numSequencePoints="2" visitedSequencePoints="1" numBranchPoints="2" visitedBranchPoints="1" />
            </CoverageSession>
            """;

        var report = CoverageReportParser.ParseXml(xml, out var error);

        Assert.Null(error);
        Assert.NotNull(report);
        Assert.Equal("OpenCover", report!.Format);
        Assert.Equal(1, report.CoveredLines);
        Assert.Equal(2, report.ValidLines);
        Assert.Equal(1, report.CoveredBranches);
        Assert.Equal(2, report.ValidBranches);
        Assert.Equal(1, Assert.Single(report.Files).CoveredLines);
        Assert.Equal(new[] { new CoverageLineSummary(1, true), new CoverageLineSummary(2, false) },
            Assert.Single(report.Files).LineDetails);
        Assert.Equal(new[] { new CoverageBranchSummary(1, 1, 2) }, Assert.Single(report.Files).BranchDetails);
    }

    [Fact]
    public void Parses_cobertura_condition_coverage_when_branch_attributes_are_missing()
    {
        const string xml = """
            <coverage>
              <packages><package><classes><class filename="A.cs">
                <lines>
                  <line number="10" hits="1" branch="true" condition-coverage="50% (1/2)" />
                  <line number="11" hits="0" branch="true" condition-coverage="100% (2/2)" />
                </lines>
              </class></classes></package></packages>
            </coverage>
            """;

        var report = CoverageReportParser.ParseXml(xml, out var error);

        Assert.Null(error);
        Assert.NotNull(report);
        Assert.Equal(3, report!.CoveredBranches);
        Assert.Equal(4, report.ValidBranches);
        Assert.Equal(new[]
        {
            new CoverageBranchSummary(10, 1, 2),
            new CoverageBranchSummary(11, 2, 2),
        }, Assert.Single(report.Files).BranchDetails);
    }

    [Fact]
    public void Rejects_unknown_or_invalid_xml_without_throwing()
    {
        Assert.Null(CoverageReportParser.ParseXml("<report />", out var unknownError));
        Assert.Contains("対応していない", unknownError);
        Assert.Null(CoverageReportParser.ParseXml("<coverage>", out var xmlError));
        Assert.False(string.IsNullOrWhiteSpace(xmlError));
    }
}
