using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// Diff ペインの表示形式の 4 択（左右／統合／全文／通し・§24.50）。左右→通しは 2 つの値の変更なので、1 つずつ書くと
/// 間に「統合」が挟まり、その通知で統合のラジオボタンが選ばれて押した「通し」が外れていた。途中の組み合わせを
/// 外へ知らせないことを確かめる。
/// </summary>
public class DiffLayoutChoiceTests
{
    private static DiffSessionViewModel CreateSut()
    {
        var workspace = new FakeWorkspaceService();
        var git = new GitService(workspace);
        return new DiffSessionViewModel(git, new FakeEditorService(), workspace, new DiffFileGateway(),
            new DiffSessionQuery(git), new DiffSessionCommandHandler(git), new LoomoSettings(),
            new GitCompareBaseViewModel(git));
    }

    /// <summary>3 択の通知が来るたびに、そのとき選ばれている形式を記録する。</summary>
    private static List<string> RecordChoices(DiffSessionViewModel sut)
    {
        var seen = new List<string>();
        sut.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DiffSessionViewModel.IsLayoutSide)
                or nameof(DiffSessionViewModel.IsLayoutUnified)
                or nameof(DiffSessionViewModel.IsLayoutFullFile)
                or nameof(DiffSessionViewModel.IsLayoutContinuous))
                seen.Add(sut.IsLayoutSide ? "左右" : sut.IsLayoutUnified ? "統合" : sut.IsLayoutFullFile ? "全文" : sut.IsLayoutContinuous ? "通し" : "なし");
        };
        return seen;
    }

    [Fact]
    public void 左右から通しへ切り替える間に統合を挟まない()
    {
        var sut = CreateSut();
        Assert.True(sut.IsLayoutSide);   // 既定は左右
        var seen = RecordChoices(sut);

        sut.IsLayoutContinuous = true;

        Assert.True(sut.IsLayoutContinuous);
        Assert.False(sut.IsLayoutSide);
        Assert.False(sut.IsLayoutUnified);
        Assert.NotEmpty(seen);
        Assert.All(seen, choice => Assert.Equal("通し", choice));
    }

    [Fact]
    public void 通しから左右へ切り替える間に統合を挟まない()
    {
        var sut = CreateSut();
        sut.IsLayoutContinuous = true;
        var seen = RecordChoices(sut);

        sut.IsLayoutSide = true;

        Assert.True(sut.IsLayoutSide);
        Assert.False(sut.IsLayoutContinuous);
        Assert.All(seen, choice => Assert.Equal("左右", choice));
    }

    [Fact]
    public void 全文は統合と排他で通しや左右から移れる()
    {
        var sut = CreateSut();
        var seen = RecordChoices(sut);

        sut.IsLayoutFullFile = true;

        Assert.True(sut.IsLayoutFullFile);
        Assert.False(sut.IsLayoutUnified);
        Assert.False(sut.IsSideBySide);
        Assert.All(seen, choice => Assert.Equal("全文", choice));

        sut.IsLayoutContinuous = true;
        Assert.True(sut.IsLayoutContinuous);
        Assert.False(sut.IsLayoutFullFile);   // 通しは全文を引き継がない（全ファイルを全文でつながない）
    }

    /// <summary>統合は変更の周り 3 行に折りたたむ。全文は変更から遠い行も出す。</summary>
    [Fact]
    public async Task 全文は変更から遠い行も統合表示に出す()
    {
        var lines = Enumerable.Range(1, 30).Select(i => $"line{i}").ToList();
        var oldText = string.Join("\n", lines);
        lines[29] = "LINE30";
        var newText = string.Join("\n", lines);

        var sut = CreateSut();
        sut.IsLayoutUnified = true;
        sut.ShowComparison(new DiffComparison("元", oldText, "案", newText));
        await WaitForRowAsync(sut, "LINE30");
        Assert.DoesNotContain(sut.DiffRows, r => r.Text == "line1");   // 折りたたまれている

        sut.IsLayoutFullFile = true;
        await WaitForRowAsync(sut, "line1");
        Assert.Contains(sut.DiffRows, r => r.Text == "LINE30");
    }

    private const string FullPatch =
        "diff --git a/x.cs b/x.cs\nindex 1111111..2222222 100644\n--- a/x.cs\n+++ b/x.cs\n@@ -1,3 +1,3 @@\n a\n-b\n+B\n c\n";

    [Fact]
    public void 全文はパッチの見出しを隠し折りたたむ統合では隠さない()
    {
        Assert.Equal(5, DiffSessionViewModel.HiddenPatchRows(FullPatch, 1_000_000));
        Assert.Equal(0, DiffSessionViewModel.HiddenPatchRows(FullPatch, 3));
        // ハンクの無いパッチ（バイナリなど）は見出しが中身なので残す
        Assert.Equal(0, DiffSessionViewModel.HiddenPatchRows("diff --git a/p.png b/p.png\nBinary files differ\n", 1_000_000));
    }

    [Fact]
    public void 見出しを隠しても行番号は1行目から数える()
    {
        var rows = FullPatch.Split('\n').Skip(5)
            .Select(raw => new DiffRowVm(sk0ya.Loomo.Core.Diff.SideBySideDiff.ClassifyPatchLine(raw).ToString(), raw) { Prefixed = true })
            .ToList();

        Assert.Equal(" a", rows[0].Text);
        Assert.Equal(1, DiffRowLineMapper.LineForUnifiedRow(rows, 0, leftSide: false));
        Assert.Equal(2, DiffRowLineMapper.LineForUnifiedRow(rows, 2, leftSide: false));   // +B は新側の 2 行目
        Assert.Equal(2, DiffRowLineMapper.LineForUnifiedRow(rows, 1, leftSide: true));    // -b は旧側の 2 行目
        Assert.Equal(3, DiffRowLineMapper.LineForUnifiedRow(rows, 3, leftSide: false));
    }

    private static async Task WaitForRowAsync(DiffSessionViewModel sut, string text)
    {
        var watch = Stopwatch.StartNew();
        while (!sut.DiffRows.Any(r => r.Text == text) && watch.Elapsed.TotalSeconds < 10)
            await Task.Delay(20);
        Assert.Contains(sut.DiffRows, r => r.Text == text);
    }

    [Fact]
    public void 外れたボタンが書くfalseでは切り替わらない()
    {
        var sut = CreateSut();
        sut.IsLayoutContinuous = true;

        sut.IsLayoutSide = false;      // ラジオボタンは選択が移るとき旧選択へ false を書く
        sut.IsLayoutUnified = false;

        Assert.True(sut.IsLayoutContinuous);
    }
}
