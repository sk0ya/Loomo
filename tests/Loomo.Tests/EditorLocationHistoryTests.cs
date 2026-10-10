using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// エディタの場所の履歴（<see cref="EditorLocationHistory"/>）と編集箇所の履歴（<see cref="EditLocationTrail"/>）。
/// 核心は「キャレットの動きだけから、飛んだ場所だけを積む」こと——打鍵や矢印移動は積まず、ファイルを開く
/// 二段の動き（前回の位置→目的の行）とページ送りの連打は1件にまとめ、戻る途中の動きで進む側を壊さない。
/// </summary>
public class EditorLocationHistoryTests
{
    private static readonly DateTime T0 = new(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);
    private static DateTime At(double seconds) => T0.AddSeconds(seconds);
    private static EditorLocation L(string path, int line, int column = 0) => new(path, line, column);

    [Fact]
    public void 近くの移動は積まずに今の項目を更新する()
    {
        var h = new EditorLocationHistory();
        h.Record(L("a.cs", 10), At(0));
        h.Record(L("a.cs", 12), At(5));
        h.Record(L("a.cs", 15, 4), At(10));

        Assert.Single(h.Entries);
        Assert.Equal(L("a.cs", 15, 4), h.Current);
        Assert.False(h.CanGoBack);
    }

    [Fact]
    public void 別ファイルへ移ると積み_戻ると元の行へ帰る()
    {
        var h = new EditorLocationHistory();
        h.Record(L("a.cs", 40, 3), At(0));
        h.Record(L("b.cs", 7), At(5));

        Assert.True(h.CanGoBack);
        Assert.Equal(L("a.cs", 40, 3), h.GoBack());
        Assert.True(h.CanGoForward);
        Assert.Equal(L("b.cs", 7), h.GoForward());
    }

    [Fact]
    public void 同じファイル内でも閾値以上離れたら積む()
    {
        var h = new EditorLocationHistory();
        h.Record(L("a.cs", 10), At(0));
        h.Record(L("a.cs", 10 + EditorLocationHistory.JumpThresholdLines), At(5));

        Assert.Equal(2, h.Entries.Count);
        Assert.Equal(L("a.cs", 10), h.GoBack());
    }

    [Fact]
    public void ファイルを開く二段の動きは1件にまとまる()
    {
        // タブが前回のキャレット（500行目）で出て、すぐ目的の行（20行目）へ飛ぶ。
        var h = new EditorLocationHistory();
        h.Record(L("a.cs", 10), At(0));
        h.Record(L("b.cs", 500), At(5));
        h.Record(L("b.cs", 20), At(5.1));

        Assert.Equal(2, h.Entries.Count);
        Assert.Equal(L("b.cs", 20), h.Current);
        Assert.Equal(L("a.cs", 10), h.GoBack());
    }

    [Fact]
    public void ページ送りの連打は1件にまとまる()
    {
        var h = new EditorLocationHistory();
        h.Record(L("a.cs", 0), At(0));
        for (var i = 1; i <= 10; i++)
            h.Record(L("a.cs", i * 20), At(5 + i * 0.3));

        Assert.Equal(2, h.Entries.Count);
        Assert.Equal(L("a.cs", 200), h.Current);
        Assert.Equal(L("a.cs", 0), h.GoBack());
    }

    [Fact]
    public void まとめる時間を過ぎた同じファイル内の大移動は別の項目になる()
    {
        var h = new EditorLocationHistory();
        h.Record(L("a.cs", 0), At(0));
        h.Record(L("a.cs", 100), At(5));
        h.Record(L("a.cs", 300), At(10));

        Assert.Equal(3, h.Entries.Count);
    }

    [Fact]
    public void 戻っている間のキャレットの動きは記録しない_進む側が残る()
    {
        var h = new EditorLocationHistory();
        h.Record(L("a.cs", 10), At(0));
        h.Record(L("b.cs", 20), At(5));

        var back = h.GoBack();
        h.BeginNavigation();
        h.Record(L("a.cs", 900), At(6));   // タブが前回の位置で出た
        h.Record(back!.Value, At(6.1));
        h.EndNavigation();

        Assert.True(h.CanGoForward);
        Assert.Equal(L("b.cs", 20), h.GoForward());
    }

    [Fact]
    public void 戻った後に別の場所へ飛ぶと進む側は捨てる()
    {
        var h = new EditorLocationHistory();
        h.Record(L("a.cs", 10), At(0));
        h.Record(L("b.cs", 20), At(5));
        h.GoBack();
        h.Record(L("c.cs", 30), At(10));

        Assert.False(h.CanGoForward);
        Assert.Equal(L("a.cs", 10), h.GoBack());
    }

    [Fact]
    public void 消えたファイルは飛ばして捨てる()
    {
        var h = new EditorLocationHistory();
        h.Record(L("a.cs", 10), At(0));
        h.Record(L("gone.cs", 20), At(5));
        h.Record(L("c.cs", 30), At(10));

        var back = h.GoBack(l => l.Path != "gone.cs");

        Assert.Equal(L("a.cs", 10), back);
        Assert.DoesNotContain(h.Entries, l => l.Path == "gone.cs");
        Assert.Equal(L("c.cs", 30), h.GoForward());
    }

    [Fact]
    public void 上限を超えたら最古を落とす()
    {
        var h = new EditorLocationHistory(capacity: 3);
        for (var i = 0; i < 5; i++)
            h.Record(L($"{i}.cs", 0), At(i * 10));

        Assert.Equal(new[] { "2.cs", "3.cs", "4.cs" }, h.Entries.Select(e => e.Path));
        Assert.Equal(L("4.cs", 0), h.Current);
    }

    [Fact]
    public void ファイル名の大小は区別しない()
    {
        var h = new EditorLocationHistory();
        h.Record(L(@"C:\w\A.cs", 10), At(0));
        h.Record(L(@"c:\w\a.cs", 12), At(5));

        Assert.Single(h.Entries);
    }

    [Fact]
    public void 最近の場所は新しい順で_近い場所と今いる場所を除く()
    {
        var h = new EditorLocationHistory();
        h.Record(L("a.cs", 10), At(0));
        h.Record(L("b.cs", 20), At(5));
        h.Record(L("a.cs", 12), At(10));   // a.cs:10 とほぼ同じ場所
        h.Record(L("c.cs", 30), At(15));   // いまここ

        var recent = h.RecentDistinct();

        Assert.Equal(new[] { L("a.cs", 12), L("b.cs", 20) }, recent);
    }
}

public class EditLocationTrailTests
{
    private static EditorLocation L(string path, int line) => new(path, line, 0);

    [Fact]
    public void 近くの連続編集は1件にまとめ_最新の位置を残す()
    {
        var t = new EditLocationTrail();
        t.Record(L("a.cs", 10));
        t.Record(L("a.cs", 11));
        t.Record(L("a.cs", 12));

        Assert.Equal(new[] { L("a.cs", 12) }, t.Entries);
    }

    [Fact]
    public void 続けて呼ぶと古い編集箇所へ遡る_今いる場所は飛ばす()
    {
        var t = new EditLocationTrail();
        t.Record(L("a.cs", 10));
        t.Record(L("b.cs", 50));
        t.Record(L("c.cs", 5));

        Assert.Equal(L("b.cs", 50), t.Previous(current: L("c.cs", 6)));
        Assert.Equal(L("a.cs", 10), t.Previous(current: L("b.cs", 50)));
        Assert.Null(t.Previous(current: L("a.cs", 10)));
    }

    [Fact]
    public void 別の場所にいるなら最新の編集箇所へ戻る()
    {
        var t = new EditLocationTrail();
        t.Record(L("a.cs", 10));
        t.Record(L("b.cs", 50));

        Assert.Equal(L("b.cs", 50), t.Previous(current: L("z.cs", 0)));
    }

    [Fact]
    public void 新しい編集が入ると遡りは先頭から()
    {
        var t = new EditLocationTrail();
        t.Record(L("a.cs", 10));
        t.Record(L("b.cs", 50));
        t.Previous(current: L("z.cs", 0));
        t.Record(L("c.cs", 5));

        Assert.Equal(L("c.cs", 5), t.Previous(current: L("z.cs", 0)));
    }

    [Fact]
    public void 同じ場所をもう一度編集したら古い記録は消して末尾へ()
    {
        var t = new EditLocationTrail();
        t.Record(L("a.cs", 10));
        t.Record(L("b.cs", 50));
        t.Record(L("a.cs", 11));

        Assert.Equal(new[] { L("b.cs", 50), L("a.cs", 11) }, t.Entries);
    }
}

public class EditorNavigationPaletteItemsTests
{
    private static Action Jump(PaletteTarget _) => () => { };

    [Fact]
    public void 最近のファイルは今のファイルと重複を除き_順序を保つ()
    {
        var items = EditorNavigationPaletteItems.ForRecentFiles(
            new[] { @"C:\w\src\A.cs", @"C:\w\src\B.cs", @"c:\w\src\a.cs", @"C:\w\C.cs" },
            activePath: @"C:\w\src\B.cs",
            toDisplayPath: p => p.Replace(@"C:\w\", "", StringComparison.OrdinalIgnoreCase),
            jump: Jump);

        Assert.Equal(new[] { "A.cs", "C.cs" }, items.Select(i => i.Title));
        Assert.Equal("src", items[0].Category);
        Assert.Equal(new PaletteTarget(@"C:\w\src\A.cs"), items[0].Target);
    }

    [Fact]
    public void 最近の場所は行の文面を名前にし_取れなければファイル名と行()
    {
        var items = EditorNavigationPaletteItems.ForLocations(
            new[] { new EditorLocation(@"C:\w\A.cs", 9, 4), new EditorLocation(@"C:\w\B.cs", 0, 0) },
            l => l.Path.EndsWith("A.cs") ? "    var x = 1;" : null,
            p => System.IO.Path.GetFileName(p), Jump);

        Assert.Equal("var x = 1;", items[0].Title);
        Assert.Equal("A.cs:10", items[0].Category);
        Assert.Equal(new PaletteTarget(@"C:\w\A.cs", 10, 5), items[0].Target);
        Assert.Equal("B.cs:1", items[1].Title);
    }

    [Fact]
    public void ファイル構造は宣言順に字下げし_キャレットを含む宣言を選べる()
    {
        var method = new OutlineNode("Run", Editor.Core.Lsp.SymbolKind.Method, 4, 8, 4, 16, Array.Empty<OutlineNode>(), "()");
        var field = new OutlineNode("_x", Editor.Core.Lsp.SymbolKind.Field, 2, 2, 2, 12, Array.Empty<OutlineNode>());
        var type = new OutlineNode("Foo", Editor.Core.Lsp.SymbolKind.Class, 0, 10, 0, 13, new[] { field, method });

        var items = EditorNavigationPaletteItems.ForStructure(new[] { type }, @"C:\w\Foo.cs", Jump);

        Assert.Equal(new[] { "Foo", "  _x", "  Run ()" }, items.Select(i => i.Title));
        Assert.Equal(new[] { "class", "field", "method" }, items.Select(i => i.Category));
        Assert.Equal(new PaletteTarget(@"C:\w\Foo.cs", 5, 17), items[2].Target);
        Assert.Equal(2, EditorNavigationPaletteItems.IndexOfEnclosing(items, new[] { type }, 6, 0));
        Assert.Equal(0, EditorNavigationPaletteItems.IndexOfEnclosing(items, new[] { type }, 9, 0));
        Assert.Equal(-1, EditorNavigationPaletteItems.IndexOfEnclosing(items, new[] { type }, 20, 0));
    }
}

public class PaletteCommandIdentityTests
{
    [Fact]
    public void 絞り込み語を書き換えてもハッシュと等価性が変わらない_回帰()
    {
        // ListBox の選択管理はハッシュで行を引く。打鍵ごとに TitleMatch が変わって値比較のハッシュが
        // ずれると、選んでいた行を見失って落ちた。
        var command = new PaletteCommand("method", "Run", () => { });
        var hash = command.GetHashCode();

        command.TitleMatch = "R";

        Assert.Equal(hash, command.GetHashCode());
        Assert.True(command.Equals(command));
    }

    [Fact]
    public void 値が同じでも別の行は別物()
    {
        Action run = () => { };
        Assert.NotEqual(new PaletteCommand("c", "t", run), new PaletteCommand("c", "t", run));
    }
}
