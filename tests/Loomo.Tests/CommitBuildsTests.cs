using System.Text.Json;
using sk0ya.Loomo.Services;
using sk0ya.Loomo.Services.GitHub;

namespace sk0ya.Loomo.Tests;

/// <summary>コミット一覧のビルド（CI）の印（§24.93）。</summary>
public sealed class CommitBuildsTests
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void ghの実行一覧を状態つきで読む()
    {
        var builds = GitHubJson.ParseRuns($$"""
            [
              {"headSha":"{{A}}","status":"completed","conclusion":"success","workflowName":"CI","url":"https://github.com/o/r/actions/runs/1"},
              {"headSha":"{{A}}","status":"in_progress","conclusion":"","workflowName":"Release","url":"https://github.com/o/r/actions/runs/2"},
              {"headSha":"{{B}}","status":"completed","conclusion":"cancelled","workflowName":"CI","url":"u"},
              {"headSha":"{{B}}","status":"completed","conclusion":"skipped","workflowName":"Docs","url":"u"}
            ]
            """);

        Assert.Equal(
            new[] { CommitBuildState.Success, CommitBuildState.Pending, CommitBuildState.Failure, CommitBuildState.Success },
            builds.Select(b => b.State));
        Assert.Equal("Release", builds[1].Name);
    }

    [Fact]
    public void AzurePipelinesのビルドを状態つきで読む()
    {
        using var doc = JsonDocument.Parse("""
            {"value":[
              {"sourceVersion":"SHA_A","status":"completed","result":"succeeded","definition":{"name":"CI"},"_links":{"web":{"href":"https://dev.azure.com/o/p/_build/results?buildId=1"}}},
              {"sourceVersion":"SHA_B","status":"inProgress","definition":{"name":"CI"}},
              {"sourceVersion":"SHA_B","status":"completed","result":"failed","definition":{"name":"Nightly"}}
            ]}
            """.Replace("SHA_A", A).Replace("SHA_B", B));

        var builds = AzureDevOpsBuildJson.Parse(doc.RootElement);

        Assert.Equal(new[] { CommitBuildState.Success, CommitBuildState.Pending, CommitBuildState.Failure }, builds.Select(b => b.State));
        Assert.Equal("https://dev.azure.com/o/p/_build/results?buildId=1", builds[0].Url);
    }

    [Fact]
    public void コミットごとに_落ちたものがあれば失敗_動いていれば待ち_全部通れば成功()
    {
        var summary = CommitBuildMarks.Summarize([
            new(A, "CI", CommitBuildState.Success, "a1"),
            new(A, "Release", CommitBuildState.Pending, "a2"),
            new(B, "CI", CommitBuildState.Failure, "b1"),
            new(B, "Docs", CommitBuildState.Success, "b2"),
        ]);

        Assert.Equal(CommitBuildState.Pending, summary[A].State);
        Assert.Equal(CommitBuildState.Failure, summary[B].State);
        // 開く先は今の状態の実行（落ちたもの・動いているもの）。
        Assert.Equal("a2", summary[A].Url);
        Assert.Equal("b1", summary[B].Url);
    }

    [Fact]
    public void やり直した同じ名前の実行は新しい方だけを数える()
    {
        // 新しい順：やり直して通ったあとに、前の失敗が残っている。
        var summary = CommitBuildMarks.Summarize([
            new(A, "CI", CommitBuildState.Success, "new"),
            new(A, "CI", CommitBuildState.Failure, "old"),
        ]);

        Assert.Equal(CommitBuildState.Success, summary[A].State);
        Assert.Single(summary[A].Builds);
    }

    [Fact]
    public void 印と説明()
    {
        Assert.Equal("✓", CommitBuildMarks.Mark(CommitBuildState.Success));
        Assert.Equal("✗", CommitBuildMarks.Mark(CommitBuildState.Failure));
        Assert.Equal("◌", CommitBuildMarks.Mark(CommitBuildState.Pending));
        var text = CommitBuildMarks.Describe(CommitBuildMarks.Summarize([new(A, "CI", CommitBuildState.Failure, "u")])[A]);
        Assert.Contains("落ちています", text);
        Assert.Contains("✗ CI", text);
        Assert.Contains("クリック", text);
    }

    [Theory]
    [InlineData("https://dev.azure.com/org/My%20Project/_git/Repo", "My Project", "Repo")]
    [InlineData("https://org@dev.azure.com/org/Proj/_git/Repo", "Proj", "Repo")]
    [InlineData("https://dev.azure.com/org/_git/Repo", "Repo", "Repo")]
    [InlineData("https://org.visualstudio.com/Proj/_git/Repo", "Proj", "Repo")]
    [InlineData("https://org.visualstudio.com/DefaultCollection/Proj/_git/Repo", "Proj", "Repo")]
    [InlineData("https://tfs.example.local/tfs/Collection/Proj/_git/Repo", "Proj", "Repo")]
    [InlineData("git@ssh.dev.azure.com:v3/org/Proj/Repo", "Proj", "Repo")]
    [InlineData("org@vs-ssh.visualstudio.com:v3/org/Proj/Repo", "Proj", "Repo")]
    public void AzureReposのリモートからプロジェクトとリポジトリを読む(string url, string project, string repository)
    {
        Assert.True(GitHostingUrl.TryGetAzureReposTarget(url, out var p, out var r));
        Assert.Equal(project, p);
        Assert.Equal(repository, r);
    }

    [Theory]
    [InlineData("https://github.com/o/r.git")]
    [InlineData("git@github.com:o/r.git")]
    [InlineData("")]
    public void AzureRepos以外は読まない(string url)
        => Assert.False(GitHostingUrl.TryGetAzureReposTarget(url, out _, out _));
}
