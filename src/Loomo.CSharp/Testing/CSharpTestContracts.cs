using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Testing;

/// <summary>ビルドを伴わず、ワークスペースのC#テストをソース走査で探索する契約。</summary>
public interface ITestDiscoveryService
{
    /// <summary>除外対象を除くC#ソースからテスト一覧を返す。</summary>
    IReadOnlyList<DiscoveredTest> Discover(string root);

    /// <summary>MSBuild評価済みの選択TFM／テストプロジェクトのCompile項目だけからテストを返す。
    /// プロジェクトが解析中または失敗中の場合は候補を返さず、古い一覧の再利用を防ぐ。</summary>
    IReadOnlyList<DiscoveredTest> Discover(SolutionModel solution);
}

/// <summary>ソース走査または公式adapter検出で得たC#テスト。</summary>
public sealed record DiscoveredTest(
    string FullyQualifiedName,
    bool IsParameterized,
    string? SourcePath = null,
    int Line1 = 0,
    string? SkipReason = null,
    IReadOnlyList<string>? Traits = null,
    IReadOnlyList<string>? Cases = null);
