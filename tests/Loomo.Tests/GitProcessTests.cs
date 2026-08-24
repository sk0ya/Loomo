namespace sk0ya.Loomo.Tests;

/// <summary>
/// 実 git プロセスを大量に起こすテストをまとめるコレクション。xUnit はテストクラスを既定で
/// 並列に走らせるが、この種のテストは<b>CPU を占有する</b>ため、同じ実行の中にいる
/// WPF のビューを組み立てて描画を待つテスト（タイミングに敏感）を巻き添えで落とす。
/// 同じコレクションに入れておくと少なくとも互いには直列化され、山が低くなる。
/// </summary>
[CollectionDefinition(Name)]
public sealed class GitProcessTests
{
    public const string Name = "git-process";
}
