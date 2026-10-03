using sk0ya.Loomo.Core.Diff;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// HEAD↔作業ツリーの変更行がステージ済みかを、2つの git 差分から割り出す対応表（<see cref="StagedChangeMap"/>）の検証。
/// </summary>
public class StagedChangeMapTests
{
    // HEAD: a b c d e f g h i j
    // インデックス: 先頭に "top" を追加（ステージ済み）→ top a b c d e f g h i j
    private const string StagedPatch =
        "--- a/f.txt\n+++ b/f.txt\n" +
        "@@ -1,3 +1,4 @@\n" +
        "+top\n" +
        " a\n b\n c\n";

    // 作業ツリー: インデックスの i を I に変更（未ステージ）→ top a b c d e f g h I j
    private const string UnstagedPatch =
        "--- a/f.txt\n+++ b/f.txt\n" +
        "@@ -7,5 +7,5 @@\n" +
        " f\n g\n h\n" +
        "-i\n+I\n" +
        " j\n";

    [Fact]
    public void ステージ済みの追加は作業ツリーの行番号で引ける()
    {
        var map = StagedChangeMap.Build(StagedPatch, UnstagedPatch);

        Assert.True(map.IsStagedAddition(1));    // top
        Assert.False(map.IsStagedAddition(10));  // I（未ステージ）
    }

    [Fact]
    public void 前のハンクの増減をまたいで行番号を写す()
    {
        var map = StagedChangeMap.Build(StagedPatch, UnstagedPatch);

        // HEAD の9行目 i は、インデックスでは先頭の追加ぶんずれて10行目。未ステージの削除として振り分く。
        var split = map.Split(headLines: [9], worktreeLines: [10]);

        Assert.Equal([10], split.Unstaged.OldLines);
        Assert.Equal([10], split.Unstaged.NewLines);
        Assert.True(split.Staged.IsEmpty);
        Assert.False(map.IsStagedRemoval(9));
    }

    [Fact]
    public void ステージ済みの追加はインデックスの行番号でアンステージへ振り分く()
    {
        var split = StagedChangeMap.Build(StagedPatch, UnstagedPatch).Split(headLines: [], worktreeLines: [1]);

        Assert.Equal([1], split.Staged.NewLines);
        Assert.True(split.Unstaged.IsEmpty);
    }

    [Fact]
    public void 何もステージしていなければ全部未ステージ()
    {
        // ステージが空なら HEAD＝インデックスなので、行番号はそのまま未ステージの差分の旧側。
        var map = StagedChangeMap.Build("", UnstagedPatch);

        Assert.False(map.IsStagedAddition(10));
        Assert.False(map.IsStagedRemoval(10));
        Assert.Equal([10], map.Split(headLines: [10], worktreeLines: []).Unstaged.OldLines);
    }

    [Fact]
    public void 新規ファイルの追加は長さ0の旧側を越えて写す()
    {
        const string newFile = "--- /dev/null\n+++ b/f.txt\n@@ -0,0 +1,2 @@\n+x\n+y\n";
        var map = StagedChangeMap.Build(newFile, "");

        Assert.True(map.IsStagedAddition(1));
        Assert.True(map.IsStagedAddition(2));
    }

    [Fact]
    public void 表示の行の印と番号をパッチから引く()
    {
        var lines = UnifiedPatchEditor.DescribeLines(UnstagedPatch);

        Assert.Equal(new UnifiedPatchEditor.PatchLine('-', 10, 0), lines[6]);
        Assert.Equal(new UnifiedPatchEditor.PatchLine('+', 0, 10), lines[7]);
        Assert.Equal('\0', lines[3].Marker);   // 文脈行
    }
}
