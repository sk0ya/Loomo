using System.Windows.Input;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// コミット一覧のキー割り当て。部屋の他の面（FolderTree）と同じ手癖——j/k で上下、gg/G で端、
/// "/" で絞り込み、Esc で解除——を、一覧の上だけ別物にしないための取り決め。
/// </summary>
public sealed class GitLogKeyMapTests
{
    private static GitLogKeyResult Resolve(Key key, ModifierKeys modifiers = ModifierKeys.None,
        bool pendingG = false, bool hasFilters = true)
        => GitSessionView.ResolveLogKey(key, modifiers, pendingG, hasFilters);

    [Theory]
    [InlineData(Key.J, GitLogKeyAction.MoveDown)]
    [InlineData(Key.K, GitLogKeyAction.MoveUp)]
    [InlineData(Key.Enter, GitLogKeyAction.OpenDiff)]
    [InlineData(Key.OemQuestion, GitLogKeyAction.FocusFilter)]
    [InlineData(Key.Escape, GitLogKeyAction.ClearFilter)]
    public void 単打の割り当て(Key key, GitLogKeyAction expected)
    {
        Assert.Equal(expected, Resolve(key).Action);
    }

    [Fact]
    public void 絞り込んでいないときのEscは何もしない()
    {
        // 解除は git への引き直し＝読み込み済みのページを全部捨てて先頭へ戻す。消すものが無いのに
        // 深く手繰った場所と選択を失う操作を、反射で押した Esc に割り当てない。
        Assert.Equal(GitLogKeyAction.None, Resolve(Key.Escape, hasFilters: false).Action);
    }

    [Fact]
    public void gの1打目は飲み込んで2打目で先頭へ飛ぶ()
    {
        // 1打目を飲み込まないと ListView の頭文字ジャンプに食われて2打目が来ない。
        var first = Resolve(Key.G);
        Assert.Equal(GitLogKeyAction.None, first.Action);
        Assert.True(first.PendingG);

        var second = Resolve(Key.G, pendingG: first.PendingG);
        Assert.Equal(GitLogKeyAction.MoveTop, second.Action);
        Assert.False(second.PendingG);
    }

    [Fact]
    public void gの後に関係ないキーが来たら待ちは解ける()
    {
        Assert.False(Resolve(Key.J, pendingG: true).PendingG);
        Assert.False(Resolve(Key.X, pendingG: true).PendingG);
    }

    [Fact]
    public void 大文字Gは末尾へ飛ぶ()
    {
        Assert.Equal(GitLogKeyAction.MoveBottom, Resolve(Key.G, ModifierKeys.Shift).Action);
        // g を待っている最中でも Shift+G は末尾（"gG" を先頭移動に化けさせない）。
        Assert.Equal(GitLogKeyAction.MoveBottom, Resolve(Key.G, ModifierKeys.Shift, pendingG: true).Action);
    }

    [Theory]
    [InlineData(Key.J, ModifierKeys.Control)]
    [InlineData(Key.C, ModifierKeys.Control)]
    [InlineData(Key.G, ModifierKeys.Control)]
    [InlineData(Key.J, ModifierKeys.Alt)]
    public void 修飾キー付きは奪わない(Key key, ModifierKeys modifiers)
    {
        // Ctrl+C（コピー）や Alt のメニュー起動まで奪うと、一覧の上だけ部屋の作法が変わる。
        var result = Resolve(key, modifiers);
        Assert.Equal(GitLogKeyAction.None, result.Action);
        Assert.False(result.PendingG);
    }

    [Fact]
    public void 割り当ての無いキーは素通りする()
    {
        Assert.Equal(GitLogKeyAction.None, Resolve(Key.A).Action);
        Assert.Equal(GitLogKeyAction.None, Resolve(Key.Space).Action);
    }
}
