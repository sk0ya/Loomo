using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// グラフでコミットを押したときの「経路」。上り（どのマージを通って先端へ届いたか）と
/// 下り（第1親の系譜）を繋いだ線の集合を、親子の組で引き当てられること。
/// </summary>
public sealed class GitCommitRouteTests
{
    private static GitLogRow Row(string hash, params string[] parents) =>
        new("", hash, hash, "t", "2026-09-19 10:00", null, hash)
        { ParentsText = string.Join(' ', parents) };

    private static GitLogRow Head(string hash, params string[] parents) =>
        Row(hash, parents) with { Refs = "HEAD -> refs/heads/main" };

    // main: h ─ m(マージ: a, f2) ─ a ─ base
    // topic:          f2 ─ f1 ─ base
    private static IReadOnlyList<GitLogRow> MergedTopic() =>
    [
        Head("h", "m"),
        Row("m", "a", "f2"),
        Row("f2", "f1"),
        Row("a", "base"),
        Row("f1", "base"),
        Row("base"),
    ];

    [Fact]
    public void 枝のコミットは取り込んだマージを通って先端へ上る()
    {
        var route = GitCommitRoute.Build(MergedTopic(), "f1")!;

        Assert.Equal("h", route.Tip);
        Assert.Equal(["h", "m", "f2"], route.Upward.Select(step => step.Hash));
        Assert.Equal(["m"], route.Merges.Select(step => step.Hash));
        // 下りは第1親の系譜。
        Assert.True(route.Contains("base"));
        // 取り込まれなかった側（main の a）は経路に乗らない。
        Assert.False(route.Contains("a"));
    }

    [Fact]
    public void 幹のコミットはマージを跨がずに上る()
    {
        var route = GitCommitRoute.Build(MergedTopic(), "a")!;

        Assert.Equal(["h", "m"], route.Upward.Select(step => step.Hash));
        Assert.Empty(route.Merges);
        Assert.False(route.Contains("f2"));
    }

    [Fact]
    public void 先端そのものなら上りは空()
    {
        var route = GitCommitRoute.Build(MergedTopic(), "h")!;

        Assert.Equal("h", route.Tip);
        Assert.Empty(route.Upward);
        // 下りは第1親を辿る＝マージの第2親側（topic）には降りない。
        Assert.True(route.Contains("a"));
        Assert.False(route.Contains("f2"));
    }

    [Fact]
    public void 一覧に居ないコミットは経路を組まない()
    {
        Assert.Null(GitCommitRoute.Build(MergedTopic(), "zzz"));
    }

    [Fact]
    public void HEADに届かない枝は一覧で一番上の先端へ上る()
    {
        // --all で別ブランチの先端 t が HEAD の系譜の外に居る。
        IReadOnlyList<GitLogRow> rows =
        [
            Row("t", "t1"),
            Head("h", "base"),
            Row("t1", "base"),
            Row("base"),
        ];

        var route = GitCommitRoute.Build(rows, "t1")!;

        Assert.Equal("t", route.Tip);
    }

    [Fact]
    public void 経路の線だけが光る()
    {
        var rows = MergedTopic();
        var graph = GitCommitGraph.Build(rows);
        var route = GitCommitRoute.Build(rows, "f1")!;

        // m の行：第2親 f2 へ降りる線は経路、第1親 a へ降りる線は経路外。
        var mergeRow = graph[1];
        var toF2 = mergeRow.Edges.Single(e => e.Kind == GitGraphEdgeKind.Out && e.Parent == "f2");
        var toA = mergeRow.Edges.Single(e => e.Kind == GitGraphEdgeKind.Out && e.Parent == "a");
        Assert.True(route.Contains(toF2));
        Assert.False(route.Contains(toA));

        // f2 から f1 へ降りる線は a の行を素通りする——その素通りの線も経路として光る。
        var aRow = graph[3];
        var passing = aRow.Edges.Where(e => e.Kind == GitGraphEdgeKind.Through).ToList();
        Assert.Contains(passing, e => e.Parent == "f1" && route.Contains(e));
    }

    [Fact]
    public void 同じ親へ合流してきた子の線も引き当てられる()
    {
        // m1 と m2 が両方 base を第1親に持つ＝base 待ちのレーンが2本。m2 の線だけを経路として光らせる。
        IReadOnlyList<GitLogRow> rows =
        [
            Head("m", "m1", "m2"),
            Row("m1", "base"),
            Row("m2", "base"),
            Row("base"),
        ];
        var graph = GitCommitGraph.Build(rows);
        var route = GitCommitRoute.Build(rows, "m2")!;

        var intoBase = graph[3].Edges.Where(e => e.Kind == GitGraphEdgeKind.In).ToList();
        Assert.Equal(2, intoBase.Count);
        Assert.Single(intoBase, e => route.Contains(e));
    }
}
