using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

/// <summary>アドホック比較（クリップボード ↔ 選択範囲など）の素材と、差分本体の行 → ファイル行の対応。</summary>
public class DiffComparisonTests
{
    [Fact]
    public void 左右を入れ替えても出どころのファイルは変わらずファイルの側だけ反転する()
    {
        var comparison = new DiffComparison(
            "選択範囲", "old", "クリップボード", "new", @"C:\work\a.cs", FileIsLeft: true);

        var swapped = comparison.Swapped();

        Assert.Equal("クリップボード", swapped.LeftTitle);
        Assert.Equal("new", swapped.LeftText);
        Assert.Equal("選択範囲", swapped.RightTitle);
        Assert.Equal("old", swapped.RightText);
        Assert.Equal(@"C:\work\a.cs", swapped.FilePath);
        Assert.False(swapped.FileIsLeft);   // 入れ替えたのでファイルの中身は右側へ移った
    }

    [Fact]
    public void 右側を差し替えると右にあったファイルの出どころは手放す()
    {
        // 2ファイル比較（右＝B.txt）を「クリップボードで再比較」した場合。右はもうファイルではないので、
        // FilePath を残すとクリップボードの行番号で B.txt を開いてしまう。
        var twoFiles = new DiffComparison("A.txt", "a", "B.txt", "b", @"C:\work\B.txt");

        var recompared = twoFiles.WithRight("クリップボード", "x");

        Assert.Equal("クリップボード", recompared.RightTitle);
        Assert.Equal("", recompared.FilePath);
    }

    [Fact]
    public void 左がファイルの比較では右を差し替えても出どころは残る()
    {
        var fileOnLeft = new DiffComparison(
            "a.cs", "a", "クリップボード", "b", @"C:\work\a.cs", FileIsLeft: true);

        var recompared = fileOnLeft.WithRight("クリップボード", "x");

        Assert.Equal(@"C:\work\a.cs", recompared.FilePath);
        Assert.True(recompared.FileIsLeft);
    }

    [Fact]
    public void 一覧の見出しは左と右の呼び名を並べる()
    {
        var comparison = new DiffComparison("a.cs", "x", "クリップボード", "y");

        Assert.Equal("a.cs ↔ クリップボード", comparison.DisplayPath);
        Assert.Contains("a.cs", comparison.Caption);
        Assert.Contains("クリップボード", comparison.Caption);
    }

    [Fact]
    public void 左右並びの行はその行の指定した側の行番号を指す()
    {
        var rows = new[]
        {
            new DiffSideRowVm("Context", "a", "Context", "a", "1", "1"),
            new DiffSideRowVm("Removed", "b", "Empty", "", "2", ""),
            new DiffSideRowVm("Context", "c", "Context", "c", "3", "2"),
        };

        Assert.Equal(1, DiffRowLineMapper.LineForSideRow(rows, 0, leftSide: false));
        Assert.Equal(2, DiffRowLineMapper.LineForSideRow(rows, 2, leftSide: false));
        Assert.Equal(3, DiffRowLineMapper.LineForSideRow(rows, 2, leftSide: true));
    }

    [Fact]
    public void 左右並びで反対側だけの行は直前のその側の行を指す()
    {
        var rows = new[]
        {
            new DiffSideRowVm("Context", "a", "Context", "a", "1", "1"),
            new DiffSideRowVm("Removed", "b", "Empty", "", "2", ""),
        };

        // 右側（新）には無い行なので直前の右行へ寄せる。左側なら自分の行番号がある。
        Assert.Equal(1, DiffRowLineMapper.LineForSideRow(rows, 1, leftSide: false));
        Assert.Equal(2, DiffRowLineMapper.LineForSideRow(rows, 1, leftSide: true));
    }

    [Fact]
    public void ファイルが左側の比較では左の行番号を読む()
    {
        // 「ファイル ↔ クリップボード」のように、右側が数行しかない比較。
        // 右側で引くと最後のクリップボード行（3行目）に張り付いてしまう。
        var rows = new[]
        {
            new DiffSideRowVm("Context", "a", "Context", "a", "1", "1"),
            new DiffSideRowVm("Context", "b", "Context", "b", "2", "2"),
            new DiffSideRowVm("Context", "c", "Context", "c", "3", "3"),
            new DiffSideRowVm("Removed", "d", "Empty", "", "4", ""),
            new DiffSideRowVm("Removed", "e", "Empty", "", "5", ""),
        };

        Assert.Equal(5, DiffRowLineMapper.LineForSideRow(rows, 4, leftSide: true));
        Assert.Equal(3, DiffRowLineMapper.LineForSideRow(rows, 4, leftSide: false));
    }

    [Fact]
    public void 統合表示はハンク見出しから新側行番号を数える()
    {
        var rows = new[]
        {
            new DiffRowVm("Header", "diff --git a/x.cs b/x.cs"),
            new DiffRowVm("Gap", "@@ -10,3 +20,4 @@"),
            new DiffRowVm("Context", " a"),   // 20行目
            new DiffRowVm("Removed", "-b"),   // 新側には無い
            new DiffRowVm("Added", "+c"),     // 21行目
            new DiffRowVm("Context", " d"),   // 22行目
        };

        Assert.Equal(20, DiffRowLineMapper.LineForUnifiedRow(rows, 2, leftSide: false));
        Assert.Equal(20, DiffRowLineMapper.LineForUnifiedRow(rows, 3, leftSide: false));
        Assert.Equal(21, DiffRowLineMapper.LineForUnifiedRow(rows, 4, leftSide: false));
        Assert.Equal(22, DiffRowLineMapper.LineForUnifiedRow(rows, 5, leftSide: false));
    }

    [Fact]
    public void 統合表示は旧側の行番号も数えられる()
    {
        var rows = new[]
        {
            new DiffRowVm("Gap", "@@ -10,3 +20,4 @@"),
            new DiffRowVm("Context", " a"),   // 旧10 / 新20
            new DiffRowVm("Removed", "-b"),   // 旧11（新側には無い）
            new DiffRowVm("Added", "+c"),     // 新21（旧側には無い）
            new DiffRowVm("Context", " d"),   // 旧12 / 新22
        };

        Assert.Equal(10, DiffRowLineMapper.LineForUnifiedRow(rows, 1, leftSide: true));
        Assert.Equal(11, DiffRowLineMapper.LineForUnifiedRow(rows, 2, leftSide: true));
        Assert.Equal(11, DiffRowLineMapper.LineForUnifiedRow(rows, 3, leftSide: true));
        Assert.Equal(12, DiffRowLineMapper.LineForUnifiedRow(rows, 4, leftSide: true));
    }

    [Fact]
    public void ハンク見出しの前の行は行番号を持たない()
    {
        var rows = new[]
        {
            new DiffRowVm("Header", "diff --git a/x.cs b/x.cs"),
            new DiffRowVm("Gap", "@@ -1,1 +1,1 @@"),
            new DiffRowVm("Context", " a"),
        };

        Assert.Equal(0, DiffRowLineMapper.LineForUnifiedRow(rows, 0, leftSide: false));
    }

    [Fact]
    public void 全文差分は先頭を1行目として省略行の分も数える()
    {
        // AI変更・アドホック比較の統合表示（@@ ではなく「… N 行省略 …」で畳まれる）
        var rows = new[]
        {
            new DiffRowVm("Context", "a"),          // 1行目
            new DiffRowVm("Gap", " … 5 行省略 …"),   // 2〜6行目
            new DiffRowVm("Added", "b"),            // 7行目
            new DiffRowVm("Context", "c"),          // 8行目
        };

        Assert.Equal(1, DiffRowLineMapper.LineForUnifiedRow(rows, 0, leftSide: false));
        Assert.Equal(7, DiffRowLineMapper.LineForUnifiedRow(rows, 2, leftSide: false));
        Assert.Equal(8, DiffRowLineMapper.LineForUnifiedRow(rows, 3, leftSide: false));
    }

    [Fact]
    public void 範囲外の行は0を返す()
    {
        Assert.Equal(0, DiffRowLineMapper.LineForUnifiedRow(Array.Empty<DiffRowVm>(), 0, leftSide: false));
        Assert.Equal(0, DiffRowLineMapper.LineForSideRow(Array.Empty<DiffSideRowVm>(), -1, leftSide: false));
    }
}
