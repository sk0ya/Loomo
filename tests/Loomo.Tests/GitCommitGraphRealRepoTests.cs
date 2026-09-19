using System.IO;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 実 git の出力に対してレーンが破綻しないこと。作り物の親子関係では出てこない並び
/// （<c>--topo-order</c> の順序・マージ・分岐したまま残る枝）を通す。
/// </summary>
[Collection(GitProcessTests.Name)]
public sealed class GitCommitGraphRealRepoTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "loomo-git-graph", Guid.NewGuid().ToString("N"));
    private GitService _git = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        _git = new GitService(workspace);
        await MustRunAsync("init");
        await MustRunAsync("symbolic-ref", "HEAD", "refs/heads/main");
        await MustRunAsync("config", "user.name", "Loomo Test");
        await MustRunAsync("config", "user.email", "loomo@example.invalid");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* git の解放待ちは無視 */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task マージのある履歴でレーンが繋がる()
    {
        await CommitAsync("base.txt", "base", "土台");
        await MustRunAsync("checkout", "-b", "topic");
        await CommitAsync("topic.txt", "topic", "枝の作業");
        await MustRunAsync("checkout", "main");
        await CommitAsync("main.txt", "main", "幹の作業");
        await MustRunAsync("merge", "--no-ff", "topic", "-m", "枝を取り込む");

        var rows = await _git.GetLogAsync(new GitLogQuery { Limit = 50 });
        var graph = GitCommitGraph.Build(rows);

        Assert.Equal(rows.Count, graph.Count);
        var commits = rows.Where(row => row.IsCommit).ToList();
        Assert.Equal(4, commits.Count);

        // マージコミットは親を2つ持ち、そこから2本の線が下へ出る。
        var mergeIndex = rows.ToList().FindIndex(row => row.Subject == "枝を取り込む");
        Assert.Equal(2, rows[mergeIndex].Parents.Count);
        var outgoing = graph[mergeIndex].Edges
            .Where(edge => edge.Kind == GitGraphEdgeKind.Out).ToList();
        Assert.Equal(2, outgoing.Count);
        Assert.Distinct(outgoing.Select(edge => edge.ToLane));

        // 土台のコミットへは両方の枝が降りてくる（＝入ってくる線が2本）。
        var baseIndex = rows.ToList().FindIndex(row => row.Subject == "土台");
        Assert.Equal(2, graph[baseIndex].Edges.Count(edge => edge.Kind == GitGraphEdgeKind.In));

        // レーン番号は必ず帯の中に収まる（はみ出すと描画で切れる）。
        for (var i = 0; i < graph.Count; i++)
        {
            // 継続行は丸を打たない（Lane = -1）。
            if (graph[i].HasNode) Assert.InRange(graph[i].Lane, 0, graph[i].LaneCount - 1);
            foreach (var edge in graph[i].Edges)
            {
                Assert.InRange(edge.FromLane, 0, graph[i].LaneCount - 1);
                Assert.InRange(edge.ToLane, 0, graph[i].LaneCount - 1);
            }
        }
    }

    [Fact]
    public async Task 分岐したままの枝は別レーンに残る()
    {
        await CommitAsync("base.txt", "base", "土台");
        await MustRunAsync("checkout", "-b", "topic");
        await CommitAsync("topic.txt", "topic", "枝の作業");
        await MustRunAsync("checkout", "main");
        await CommitAsync("main.txt", "main", "幹の作業");

        var rows = await _git.GetLogAsync(new GitLogQuery { Limit = 50 });
        var graph = GitCommitGraph.Build(rows);

        // 合流していないので、どこかの行で2本のレーンが並ぶ。
        Assert.Contains(graph, row => row.LaneCount >= 2);
        // 最後（＝いちばん古い土台）へは2本が降りてくる。
        Assert.Equal(2, graph[^1].Edges.Count(edge => edge.Kind == GitGraphEdgeKind.In));
    }

    private async Task CommitAsync(string path, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, path), content);
        await MustRunAsync("add", path);
        await MustRunAsync("commit", "-m", message);
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, $"git {string.Join(' ', args)}: {result.Error}");
        return result;
    }
}
