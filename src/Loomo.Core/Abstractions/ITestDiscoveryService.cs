using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.Core.Abstractions;

/// <summary>ビルドを伴わず、ワークスペースのテストをソース走査で探索する。</summary>
public interface ITestDiscoveryService
{
    /// <summary>除外対象を除く C# ソースからテスト一覧を返す。</summary>
    IReadOnlyList<DiscoveredTest> Discover(string root);
}
