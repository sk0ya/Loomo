using System.Linq;
using sk0ya.Loomo.CSharp.Editor;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 右クリックの「C#」サブメニューの並び。C# の操作は 40 種あるが、右クリックはその目録ではない——
/// 開いてすぐ見えるのは毎日使うものだけにして、残りは入れ子へ落とす。ここではその「表の段の短さ」と、
/// 選択が要る操作の出し分け（実行側が <c>HasSelection</c> 必須のものを押させない。設計書 §23.3）、
/// 見出し・キー表記がカタログ由来であることを見る。
/// </summary>
public sealed class CSharpEditorMenuTests
{
    private static string[] PrimaryIds(CSharpMenuPlan plan)
        => plan.Primary.SelectMany(section => section.Entries)
            .Select(entry => entry.CommandId).ToArray();

    private static string[] AllIds(CSharpMenuPlan plan)
        => PrimaryIds(plan)
            .Concat(plan.MoreRewrite.Concat(plan.MoreGenerate).Concat(plan.Tidy)
                .Select(entry => entry.CommandId))
            .ToArray();

    /// <summary>表の段は一目で読める長さに収める。ここが伸びたら何かを入れ子へ落とす合図。</summary>
    [Fact]
    public void 開いてすぐ見える段は選択の有無にかかわらず8項目以内()
    {
        Assert.InRange(PrimaryIds(CSharpEditorMenu.Build(hasSelection: false)).Length, 1, 8);
        Assert.InRange(PrimaryIds(CSharpEditorMenu.Build(hasSelection: true)).Length, 1, 8);
    }

    /// <summary>毎日使うものが表、たまに使うものは入れ子。</summary>
    [Fact]
    public void 表の段には代表操作だけを置く()
    {
        var primary = PrimaryIds(CSharpEditorMenu.Build(hasSelection: true));

        Assert.Contains(CSharpEditorCommandCatalog.OrganizeUsings, primary);
        Assert.Contains(CSharpEditorCommandCatalog.ExtractMethod, primary);
        Assert.Contains(CSharpEditorCommandCatalog.IntroduceVariable, primary);
        Assert.Contains(CSharpEditorCommandCatalog.ImplementInterface, primary);
        // 目録側（たまに使う生成・書き換え・範囲操作）は表に出さない。
        Assert.DoesNotContain(CSharpEditorCommandCatalog.GenerateDisposePattern, primary);
        Assert.DoesNotContain(CSharpEditorCommandCatalog.PullUp, primary);
        Assert.DoesNotContain(CSharpEditorCommandCatalog.Cleanup, primary);
    }

    [Fact]
    public void 選択が無いときは選択を要する操作を一つも出さない()
    {
        var ids = AllIds(CSharpEditorMenu.Build(hasSelection: false));

        Assert.DoesNotContain(CSharpEditorCommandCatalog.ExtractMethod, ids);
        Assert.DoesNotContain(CSharpEditorCommandCatalog.IntroduceVariable, ids);
        Assert.DoesNotContain(CSharpEditorCommandCatalog.SafeDelete, ids);
        Assert.DoesNotContain(CSharpEditorCommandCatalog.GenerateJsonTypes, ids);
        // キャレットだけで動く整理・生成は残る（メニューが空になっては困る）。
        Assert.Contains(CSharpEditorCommandCatalog.OrganizeUsings, ids);
        Assert.Contains(CSharpEditorCommandCatalog.GenerateConstructor, ids);
    }

    /// <summary>「書き換え」入れ子は中身が全部選択必須なので、選択が無いときは空＝親項目ごと出さない。</summary>
    [Fact]
    public void 選択が無いとき書き換えの入れ子は空になる()
    {
        Assert.Empty(CSharpEditorMenu.Build(hasSelection: false).MoreRewrite);
        Assert.NotEmpty(CSharpEditorMenu.Build(hasSelection: true).MoreRewrite);
    }

    /// <summary>入れ子へ落ちた操作も、コマンドパレット・キーバインドからは 1 手で届く
    /// （落としたのは並びだけで、カタログからは外していない）。</summary>
    [Fact]
    public void 入れ子へ落とした操作もカタログには残っている()
    {
        var ids = AllIds(CSharpEditorMenu.Build(hasSelection: true));
        var catalog = CSharpEditorCommandCatalog.All.Select(command => command.Id).ToHashSet();

        Assert.All(ids, id => Assert.Contains(id, catalog));
        Assert.Contains(CSharpEditorCommandCatalog.GenerateDisposePattern, ids);
        Assert.Contains(CSharpEditorCommandCatalog.SafeDelete, ids);
    }

    /// <summary>見出しはカタログの名前そのまま（入力を尋ねる操作だけ末尾に「…」）。
    /// メニュー側で文字列を持ち直すとコマンドパレットと表記がズレる。</summary>
    [Fact]
    public void 見出しはカタログの名前から作る()
    {
        Assert.Equal("選択範囲からメソッドを抽出…",
            CSharpEditorMenu.HeaderFor(CSharpEditorCommandCatalog.ExtractMethod));
        Assert.Equal("ToStringを生成",
            CSharpEditorMenu.HeaderFor(CSharpEditorCommandCatalog.GenerateToString));
        Assert.Equal("名前を変更…", CSharpEditorMenu.HeaderFor(CSharpEditorCommandCatalog.Rename));
    }

    [Fact]
    public void キー表記はカタログの既定バインドから引く()
    {
        Assert.Equal("Ctrl+Alt+M", CSharpEditorMenu.GestureFor(CSharpEditorCommandCatalog.ExtractMethod));
        Assert.Equal("Shift+F6", CSharpEditorMenu.GestureFor(CSharpEditorCommandCatalog.Rename));
        Assert.Null(CSharpEditorMenu.GestureFor(CSharpEditorCommandCatalog.GenerateToString));
    }

    /// <summary>同じ操作が表と入れ子の両方に出ない（写し間違いの検出）。</summary>
    [Fact]
    public void 同じ操作を二度並べない()
    {
        var ids = AllIds(CSharpEditorMenu.Build(hasSelection: true));

        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    /// <summary>「定義をPeek表示」はこのサブメニューではなく、コントロール側の「移動」へ入れる
    /// （移動の入口を 2 か所に割らない）。</summary>
    [Fact]
    public void 定義のPeekはCシャープのサブメニューには出さない()
    {
        Assert.DoesNotContain(CSharpEditorCommandCatalog.PeekDefinition,
            AllIds(CSharpEditorMenu.Build(hasSelection: true)));
        Assert.DoesNotContain(CSharpEditorCommandCatalog.PeekDefinition,
            AllIds(CSharpEditorMenu.Build(hasSelection: false)));
    }
}
