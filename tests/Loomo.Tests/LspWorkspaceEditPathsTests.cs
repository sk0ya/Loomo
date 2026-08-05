using System;
using System.IO;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// rename／複数ファイルのコードアクションで、エディタから渡された URI をどのファイルとして
/// 書き換えるかの解決（<see cref="LspWorkspaceEditPaths"/>）。
///
/// <para>tsserver 系は <c>file:///c%3A/…</c> を返す。ここを <c>new Uri(uri).LocalPath</c> で
/// 変換していた頃は <c>/c:/…</c> → <c>Path.GetFullPath</c> → <c>C:\c:\…</c> となり、
/// ワークスペース外判定に落ちて TypeScript の rename が必ず失敗していた。</para>
/// </summary>
public class LspWorkspaceEditPathsTests
{
    private const string Root = @"C:\work\app";
    private const string Added = @"D:\shared\lib";
    private static readonly string[] Roots = [Root];

    [Fact]
    public void パーセント符号化されたドライブのURIもワークスペース内として解決できる()
    {
        var path = LspWorkspaceEditPaths.ResolveInWorkspace("file:///c%3A/work/app/src/a.ts", Roots);

        Assert.Equal(@"c:\work\app\src\a.ts", path, ignoreCase: true);
    }

    [Fact]
    public void 素のドライブ表記のURIも同じパスに解決される()
    {
        Assert.Equal(
            LspWorkspaceEditPaths.ResolveInWorkspace("file:///C:/work/app/src/a.ts", Roots),
            LspWorkspaceEditPaths.ResolveInWorkspace("file:///c%3A/work/app/src/a.ts", Roots),
            ignoreCase: true);
    }

    [Fact]
    public void 空白や日本語を含むパスも復元できる()
    {
        var path = LspWorkspaceEditPaths.ResolveInWorkspace(
            "file:///c%3A/work/app/src/%E3%83%A1%E3%83%A2%20a.ts", Roots);

        Assert.Equal(@"c:\work\app\src\メモ a.ts", path, ignoreCase: true);
    }

    [Fact]
    public void ワークスペース外のファイルは拒否する()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => LspWorkspaceEditPaths.ResolveInWorkspace("file:///c%3A/other/b.ts", Roots));

        Assert.Contains("ワークスペース外", ex.Message);
    }

    [Theory]
    [InlineData("untitled:Untitled-1")]
    [InlineData("https://example.com/a.ts")]
    [InlineData("")]
    public void ファイルでないURIは拒否する(string uri)
    {
        Assert.Throws<InvalidOperationException>(
            () => LspWorkspaceEditPaths.ResolveInWorkspace(uri, Roots));
    }

    [Fact]
    public void ルート末尾の区切り文字の有無で判定が変わらない()
    {
        const string uri = "file:///c%3A/work/app/src/a.ts";

        Assert.Equal(
            LspWorkspaceEditPaths.ResolveInWorkspace(uri, [@"C:\work\app"]),
            LspWorkspaceEditPaths.ResolveInWorkspace(uri, [@"C:\work\app\"]),
            ignoreCase: true);
    }

    [Fact]
    public void 同名で始まる別フォルダーをワークスペース内と誤認しない()
    {
        Assert.Throws<InvalidOperationException>(
            () => LspWorkspaceEditPaths.ResolveInWorkspace("file:///c%3A/work/app2/a.ts", Roots));
    }

    /// <summary>ワークスペースはマルチルート（プライマリ＋あとから追加したフォルダー）。
    /// プライマリだけで判定していた頃は、追加フォルダーのファイルが「ワークスペース外」になり、
    /// そこを含む rename／リファクタリングが丸ごと失敗していた。</summary>
    [Fact]
    public void あとから追加したフォルダーのファイルもワークスペース内として解決できる()
    {
        var path = LspWorkspaceEditPaths.ResolveInWorkspace(
            "file:///d%3A/shared/lib/util.ts", [Root, Added]);

        Assert.Equal(@"d:\shared\lib\util.ts", path, ignoreCase: true);
    }

    [Fact]
    public void どのフォルダーにも属さないファイルは依然として拒否する()
    {
        Assert.Throws<InvalidOperationException>(
            () => LspWorkspaceEditPaths.ResolveInWorkspace("file:///e%3A/elsewhere/x.ts", [Root, Added]));
    }

    [Fact]
    public void フォルダーが1つも開かれていなければ何も書き換えない()
    {
        Assert.Throws<InvalidOperationException>(
            () => LspWorkspaceEditPaths.ResolveInWorkspace("file:///c%3A/work/app/src/a.ts", []));
    }

    [Theory]
    [InlineData(@"C:\work\app\src\a.ts", true)]
    [InlineData(@"D:\shared\lib\util.ts", true)]
    [InlineData(@"C:\work\app2\a.ts", false)]
    public void 所属判定は全フォルダーを見る(string path, bool expected)
        => Assert.Equal(expected, sk0ya.Loomo.Core.Files.WorkspacePaths.Contains([Root, Added], path));
}
