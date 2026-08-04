using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// CSV/TSV グリッド（VGrid）の検索ハイライト。グリッドはセル単位で塗るので、
/// 判定は「そのセル値がクエリを含むか」になる。
/// </summary>
public sealed class VGridSearchHighlightTests
{
    [Fact]
    public void 大小を区別しない既定では部分一致で塗る()
    {
        var matches = VGridTextSync.BuildCellMatcher("loomo", caseSensitive: false, useRegex: false);

        Assert.NotNull(matches);
        Assert.True(matches!("sk0ya.LOOMO.App"));
        Assert.False(matches("terminal"));
    }

    [Fact]
    public void 大小区別をオンにすると一致しなくなる()
    {
        var matches = VGridTextSync.BuildCellMatcher("loomo", caseSensitive: true, useRegex: false);

        Assert.NotNull(matches);
        Assert.False(matches!("LOOMO"));
        Assert.True(matches("loomo"));
    }

    [Fact]
    public void 正規表現モードでは式として一致を見る()
    {
        var matches = VGridTextSync.BuildCellMatcher(@"^\d{4}-\d{2}$", caseSensitive: false, useRegex: true);

        Assert.NotNull(matches);
        Assert.True(matches!("2026-07"));
        Assert.False(matches("2026/07"));
    }

    [Fact]
    public void 入力途中の不正な正規表現は塗らない()
        => Assert.Null(VGridTextSync.BuildCellMatcher("(unclosed", caseSensitive: false, useRegex: true));

    [Fact]
    public void 非正規表現ならメタ文字はリテラル扱い()
    {
        var matches = VGridTextSync.BuildCellMatcher("a.c", caseSensitive: false, useRegex: false);

        Assert.NotNull(matches);
        Assert.True(matches!("xa.cy"));
        Assert.False(matches("abc"));
    }

    [Fact]
    public void VGridの表示インスタンスは検索ハイライトを受け取れる()
        => RunSta(() =>
        {
            // 塗る口は提供者（工場）ではなく表示インスタンス側にある。表示面ごとに実体が分かれた以上、
            // 条件は実体へ配らないと「複製窓のグリッドだけ塗られない」が起きる。
            using var visual = new VGridEditorSupport(new LoomoSettings()).CreateVisual();
            Assert.IsAssignableFrom<IEditorSupportSearchHighlightTarget>(visual);
        });

    private static void RunSta(Action action)
    {
        Exception? ex = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { action(); }
            catch (Exception e) { ex = e; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (ex is not null) throw ex;
    }
}
