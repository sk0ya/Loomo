using System.Linq;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Diff;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>アドホック比較（クリップボード ↔ 選択範囲など）の素材と、差分本体の行 → ファイル行の対応。</summary>
public class DiffComparisonTests
{
    /// <summary>比較モードは git にも変更ジャーナルにも触らないので、実体のまま組み立ててよい。</summary>
    private static DiffSessionViewModel CreateSut()
    {
        var workspace = new FakeWorkspaceService();
        var journal = new FileChangeJournal();
        var git = new GitService(workspace);
        var files = new DiffFileGateway();
        return new DiffSessionViewModel(journal, git, new FakeEditorService(), workspace, files,
            new DiffSessionQuery(journal, git), new DiffSessionCommandHandler(files, journal, git));
    }

    private static DiffComparison Compare(string name)
        => new(name, $"{name} の元", "クリップボード", $"{name} の案");

    [Fact]
    public void 比較は積み増され前の比較は消えない()
    {
        var sut = CreateSut();

        sut.ShowComparison(Compare("A"));
        sut.ShowComparison(Compare("B"));

        Assert.Equal(2, sut.Files.Count);
        Assert.Equal(new[] { "B ↔ クリップボード", "A ↔ クリップボード" },
            sut.Files.Select(f => f.DisplayPath));   // 新しいものが上
        Assert.Equal("B ↔ クリップボード", sut.SelectedFile?.DisplayPath);   // 送った比較を見せる
        Assert.Equal("比較（2件）", sut.FileListHeader);
    }

    [Fact]
    public void 同じ素材を二度送っても二重に並ばない()
    {
        var sut = CreateSut();

        sut.ShowComparison(Compare("A"));
        sut.ShowComparison(Compare("B"));
        sut.ShowComparison(Compare("A"));   // 中身の同じ比較（別インスタンス）

        Assert.Equal(2, sut.Files.Count);
        Assert.Equal("A ↔ クリップボード", sut.SelectedFile?.DisplayPath);   // 既にある方へ戻る
    }

    [Fact]
    public void 閉じるとその比較だけが一覧から消える()
    {
        var sut = CreateSut();
        sut.ShowComparison(Compare("A"));
        sut.ShowComparison(Compare("B"));

        sut.CloseComparisonCommand.Execute(sut.Files.Single(f => f.DisplayPath.StartsWith("B")));

        Assert.Equal("A ↔ クリップボード", Assert.Single(sut.Files).DisplayPath);
        Assert.Equal("A ↔ クリップボード", sut.SelectedFile?.DisplayPath);   // 閉じた位置の隣へ寄る
    }

    [Fact]
    public void 最後の比較を閉じると作り方の案内へ戻る()
    {
        var sut = CreateSut();
        sut.ShowComparison(Compare("A"));

        sut.CloseComparisonCommand.Execute(null);   // 引数なし＝今見ている比較

        Assert.Empty(sut.Files);
        Assert.Null(sut.SelectedFile);
        Assert.False(sut.HasComparison);
        Assert.Equal("", sut.CompareCaption);       // 帯を出したままにしない
        Assert.Contains("比較する内容がありません", sut.EmptyMessage);
    }

    [Fact]
    public void 左右入替は積み増しではなく同じ位置の置き換え()
    {
        var sut = CreateSut();
        sut.ShowComparison(Compare("A"));
        sut.ShowComparison(Compare("B"));
        sut.SelectedFile = sut.Files.Single(f => f.DisplayPath.StartsWith("A"));

        sut.SwapComparisonCommand.Execute(null);

        Assert.Equal(2, sut.Files.Count);
        Assert.Equal(new[] { "B ↔ クリップボード", "クリップボード ↔ A" },
            sut.Files.Select(f => f.DisplayPath));   // A は元の位置のまま入れ替わる
        Assert.Equal("クリップボード ↔ A", sut.SelectedFile?.DisplayPath);
    }

    [Fact]
    public void 見ている比較が帯と比較専用操作の対象になる()
    {
        var sut = CreateSut();
        sut.ShowComparison(Compare("A"));
        sut.ShowComparison(Compare("B"));

        Assert.Contains("B", sut.CompareCaption);

        sut.SelectedFile = sut.Files.Single(f => f.DisplayPath.StartsWith("A"));

        Assert.True(sut.HasComparison);
        Assert.Contains("A", sut.CompareCaption);

        // 別のソースへ切り替えれば比較専用の操作は死ぬ（押した瞬間に見ている差分が消えないように）が、素材は残る。
        sut.IsAiMode = true;
        Assert.False(sut.HasComparison);
        Assert.Equal("", sut.CompareCaption);
        sut.IsCompareMode = true;
        Assert.Equal(2, sut.Files.Count);
    }

    [Fact]
    public void すべて閉じると比較は残らない()
    {
        var sut = CreateSut();
        sut.ShowComparison(Compare("A"));
        sut.ShowComparison(Compare("B"));

        sut.CloseAllComparisonsCommand.Execute(null);

        Assert.Empty(sut.Files);
        Assert.Equal("比較（0件）", sut.FileListHeader);
    }

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
