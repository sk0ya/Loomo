using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;
using sk0ya.Loomo.Services.Refactoring;
using Xunit;

namespace sk0ya.Loomo.Tests;

public sealed class RefactoringMenuTests
{
    private static LspCodeAction Action(string title, string? kind = null, string? disabled = null) =>
        new(title, kind, Edit: null, Command: null, RawJson: null, IsPreferred: false, DisabledReason: disabled);

    [Fact]
    public void Quick_fixes_never_reach_the_refactoring_menu()
    {
        var groups = RefactoringMenu.Build([
            Action("using System; を追加", LspCodeActionKinds.QuickFix),
            Action("using の整理", "source.organizeImports"),
            Action("Extract method", LspCodeActionKinds.RefactorExtract),
        ]);

        var item = Assert.Single(groups.SelectMany(g => g.Items));
        Assert.Equal("メソッドの抽出", item.Title);
    }

    /// <summary>サーバーが「この選択では使えない」と言っている項目を並べると、
    /// 押せないメニューでメニューが埋まる。</summary>
    [Fact]
    public void Disabled_actions_are_dropped()
    {
        var groups = RefactoringMenu.Build([
            Action("Inline variable", LspCodeActionKinds.RefactorInline, disabled: "選択が式ではありません"),
        ]);

        Assert.Empty(groups);
    }

    [Fact]
    public void Groups_come_out_in_extract_inline_move_rewrite_order()
    {
        var groups = RefactoringMenu.Build([
            Action("Convert to switch", LspCodeActionKinds.RefactorRewrite),
            Action("Inline method", LspCodeActionKinds.RefactorInline),
            Action("Move type to Foo.cs", LspCodeActionKinds.RefactorMove),
            Action("Extract method", LspCodeActionKinds.RefactorExtract),
        ]);

        Assert.Equal(
            [RefactoringGroup.Extract, RefactoringGroup.Inline, RefactoringGroup.Move, RefactoringGroup.Rewrite],
            groups.Select(g => g.Group));
    }

    /// <summary>kind を申告しないサーバー（旧 Command 形式）でも候補を落とさない。</summary>
    [Fact]
    public void Actions_without_a_kind_are_classified_by_their_title()
    {
        var groups = RefactoringMenu.Build([
            Action("Extract into function"),
            Action("Inline variable"),
            Action("Do something odd"),
        ]);

        var byGroup = groups.ToDictionary(g => g.Group, g => g.Items);
        Assert.Single(byGroup[RefactoringGroup.Extract]);
        Assert.Single(byGroup[RefactoringGroup.Inline]);
        Assert.Single(byGroup[RefactoringGroup.Other]);
    }

    [Theory]
    [InlineData("Extract method", "メソッドの抽出")]
    [InlineData("Extract interface...", "インターフェースの抽出…")]
    [InlineData("Extract base class...", "基底クラスの抽出…")]
    [InlineData("Inline temporary variable", "一時変数のインライン化")]
    [InlineData("Move to a new file", "新しいファイルへ移動")]
    [InlineData("Extract to function in module scope", "関数へ抽出（in module scope）")]
    [InlineData("Introduce local for 'a + b'", "ローカル変数の導入（'a + b'）")]
    public void Well_known_titles_are_shown_in_japanese(string serverTitle, string expected)
        => Assert.Equal(expected, RefactoringMenu.Localize(serverTitle));

    /// <summary>知らない題を当てずっぽうで訳すより、原文のまま出したほうが誤解が少ない。</summary>
    [Fact]
    public void Unknown_titles_are_left_as_the_server_wrote_them()
        => Assert.Equal("Wrap in Result<T>", RefactoringMenu.Localize("Wrap in Result<T>"));

    [Fact]
    public void The_server_title_is_kept_alongside_the_translation()
    {
        var groups = RefactoringMenu.Build([Action("Extract method", LspCodeActionKinds.RefactorExtract)]);
        var item = Assert.Single(groups.SelectMany(g => g.Items));

        Assert.Equal("メソッドの抽出", item.Title);
        Assert.Equal("Extract method", item.ServerTitle);
    }
}
