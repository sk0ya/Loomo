using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 親子関係から描画用のレーンを組む純ロジック。<c>git log --graph</c> の ASCII を読むのをやめた
/// 代わりなので、ここが枝分かれとマージの見え方を全部決める。
/// </summary>
public sealed class GitCommitGraphTests
{
    private static GitLogRow Row(string hash, params string[] parents) =>
        new("", hash, hash, "t", "2026-09-19 10:00", null, hash) { Parents = parents };

    [Fact]
    public void 一本道は同じレーンに並ぶ()
    {
        var graph = GitCommitGraph.Build([Row("c", "b"), Row("b", "a"), Row("a")]);

        Assert.All(graph, row => Assert.Equal(0, row.Lane));
        Assert.All(graph, row => Assert.Equal(1, row.LaneCount));
        // 最後のコミットは親が無いので、下へ伸びる線が無い（上から入ってくる線だけ）。
        Assert.DoesNotContain(graph[^1].Edges, e => e.Kind == GitGraphEdgeKind.Out);
        Assert.Contains(graph[^1].Edges, e => e.Kind == GitGraphEdgeKind.In);
    }

    [Fact]
    public void 枝分かれは別のレーンへ逃がす()
    {
        // topic(t1) と main(m1) が両方 base(b) を親に持つ（＝分岐している）。
        var graph = GitCommitGraph.Build([Row("t1", "b"), Row("m1", "b"), Row("b")]);

        Assert.Equal(0, graph[0].Lane);
        Assert.Equal(1, graph[1].Lane);
        Assert.Equal(2, graph[1].LaneCount);
        // base は先にレーン0が待っているので、そこへ着地して2本が1本になる。
        Assert.Equal(0, graph[2].Lane);
        // 着地する行の帯にはレーン1から降りてくる線が居るので、幅はまだ2
        // （狭めるとその線が描かれない）。詰まるのは次の行から＝閉じた枝のぶん幅は詰まる。
        Assert.Equal(2, graph[2].LaneCount);
        Assert.Contains(graph[2].Edges, e => e.Kind == GitGraphEdgeKind.In && e.FromLane == 1);
    }

    [Fact]
    public void 枝ごとに色が変わる()
    {
        var graph = GitCommitGraph.Build([Row("t1", "b"), Row("m1", "b"), Row("b")]);

        Assert.NotEqual(graph[0].Color, graph[1].Color);
        // 同じ枝が下へ続く間は色を引き継ぐ（レーン番号で色を決めると、枝が閉じて番号が
        // 詰まるたびに既存の線の色が変わってしまう）。
        Assert.Equal(graph[0].Color, graph[2].Color);
    }

    [Fact]
    public void マージは両方の親へ線を引く()
    {
        // m はマージコミット（第1親 a・第2親 b）。
        var graph = GitCommitGraph.Build([Row("m", "a", "b"), Row("a", "z"), Row("b", "z"), Row("z")]);

        var merge = graph[0];
        Assert.Equal(0, merge.Lane);
        // 第1親は自分のレーンをそのまま、第2親は別レーンへ分かれる。
        Assert.Contains(merge.Edges, e => e.Kind == GitGraphEdgeKind.Out && e.ToLane == 0);
        var toSecond = Assert.Single(merge.Edges, e => e.Kind == GitGraphEdgeKind.Out && e.ToLane == 1);
        Assert.Equal(0, toSecond.FromLane);
        // 第2親のために開いたレーンは「素通り」ではない（この行から始まる線）。
        Assert.DoesNotContain(merge.Edges, e => e.Kind == GitGraphEdgeKind.Through);
        Assert.Equal(2, graph[1].LaneCount);

        // 2本は共通の親 z で再び1本になる（合流する行の帯は、降りてくる線のぶんまだ幅2）。
        Assert.Equal(0, graph[3].Lane);
        Assert.Equal(2, graph[3].LaneCount);
        Assert.Contains(graph[3].Edges, e => e.Kind == GitGraphEdgeKind.In && e.FromLane == 1);
    }

    [Fact]
    public void 素通りの線は丸を経由しない印を持つ()
    {
        // t1 の行では、m1 を待っているレーンがもう1本走っている。
        var graph = GitCommitGraph.Build(
            [Row("t1", "t0"), Row("m1", "b"), Row("t0", "b"), Row("b")]);

        // 1行目は自分だけ（まだ他の枝は開いていない）。
        Assert.DoesNotContain(graph[0].Edges, e => e.Kind == GitGraphEdgeKind.Through);
        // 3行目（t0）では m1 由来のレーンが素通りする。
        Assert.Contains(graph[2].Edges, e => e.Kind == GitGraphEdgeKind.Through);
    }

    [Fact]
    public void 一覧の外にいる親でも落ちない()
    {
        // 読み込んでいない古いコミット（次のページ）や浅いクローンの端。
        var graph = GitCommitGraph.Build([Row("c", "未読の親")]);

        var row = Assert.Single(graph);
        Assert.Equal(0, row.Lane);
        // 線は下端まで伸びる（そこで途切れて見える）。
        Assert.Contains(row.Edges, e => e.Kind == GitGraphEdgeKind.Out && e.ToLane == 0);
    }

    [Fact]
    public void 空の一覧は空を返す()
    {
        Assert.Empty(GitCommitGraph.Build([]));
    }

    [Fact]
    public void 閉じた枝のぶん幅は詰まる()
    {
        // t1 が b で終わると、レーン1は空く。その後の行は幅1に戻る。
        var graph = GitCommitGraph.Build([Row("m1", "b"), Row("t1", "b"), Row("b", "a"), Row("a")]);

        Assert.Equal(2, graph[1].LaneCount);
        Assert.Equal(1, graph[3].LaneCount);
    }
}
