using System;
using System.Collections.Generic;
using System.IO;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Files;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ワークスペース＝**フォルダーの集合**に対するパスの問い（<see cref="WorkspacePaths"/>）。
///
/// <para>この判定は以前 8 箇所以上で個別に書かれていて、常習の間違いが2つあった:
/// ①プライマリだけを見て追加フォルダーを取りこぼす、②区切り文字を付けずに前方一致して
/// <c>C:\work\app2</c> を <c>C:\work\app</c> 配下と誤認する。実装を1つに集めたので、
/// ここが両方を代表して押さえる。</para>
/// </summary>
public sealed class WorkspacePathsTests
{
    private const string Primary = @"C:\work\app";
    private const string Added = @"D:\shared\lib";
    private static readonly string[] Both = [Primary, Added];

    [Theory]
    [InlineData(@"C:\work\app\src\a.cs", true)]
    [InlineData(@"C:\work\app", true)]                 // フォルダー自身
    [InlineData(@"C:\work\app\", true)]                // 末尾区切りあり
    [InlineData(@"C:\work\app2\a.cs", false)]          // 名前が前方一致する別フォルダー
    [InlineData(@"C:\work\application\a.cs", false)]
    [InlineData(@"C:\other\a.cs", false)]
    public void 単一フォルダーの包含判定(string path, bool expected)
        => Assert.Equal(expected, WorkspacePaths.IsWithin(Primary, path));

    /// <summary>あとから追加したフォルダーも同格。ここを落とすと rename／リファクタリング／
    /// AI のファイル書き込みが「ワークスペース外」で失敗する。</summary>
    [Theory]
    [InlineData(@"C:\work\app\src\a.cs", true)]
    [InlineData(@"D:\shared\lib\util.cs", true)]
    [InlineData(@"C:\work\app2\a.cs", false)]
    [InlineData(@"E:\elsewhere\x.cs", false)]
    public void 全フォルダーを見る(string path, bool expected)
        => Assert.Equal(expected, WorkspacePaths.Contains(Both, path));

    [Fact]
    public void フォルダーが無ければ何も含まない()
    {
        Assert.False(WorkspacePaths.Contains([], @"C:\work\app\a.cs"));
        Assert.False(WorkspacePaths.Contains(null, @"C:\work\app\a.cs"));
    }

    [Fact]
    public void 担当フォルダーを返す()
    {
        Assert.Equal(Added, WorkspacePaths.FolderFor(Both, @"D:\shared\lib\util.cs"));
        Assert.Equal(Primary, WorkspacePaths.FolderFor(Both, @"C:\work\app\src\a.cs"));
        Assert.Null(WorkspacePaths.FolderFor(Both, @"E:\elsewhere\x.cs"));
    }

    /// <summary>入れ子は WorkspaceService が防ぐが、万一あればより深い方を担当にする
    /// （言語サーバーのルートがぶれない）。</summary>
    [Fact]
    public void 入れ子ならより深いフォルダーが担当()
        => Assert.Equal(@"C:\work\app\sub",
            WorkspacePaths.FolderFor([@"C:\work\app", @"C:\work\app\sub"], @"C:\work\app\sub\a.cs"));

    [Fact]
    public void 単一フォルダーならフォルダー名を前置しない()
        => Assert.Equal("src/a.cs", WorkspacePaths.ToDisplayPath([Primary], @"C:\work\app\src\a.cs"));

    [Fact]
    public void 複数フォルダーならフォルダー名を前置する()
    {
        Assert.Equal("app/src/a.cs", WorkspacePaths.ToDisplayPath(Both, @"C:\work\app\src\a.cs"));
        Assert.Equal("lib/util.cs", WorkspacePaths.ToDisplayPath(Both, @"D:\shared\lib\util.cs"));
    }

    [Fact]
    public void フォルダー自身は複数時にフォルダー名だけになる()
        => Assert.Equal("app", WorkspacePaths.ToDisplayPath(Both, @"C:\work\app"));

    /// <summary>ワークスペース外は短くせず絶対パスのまま——出所を隠さない。</summary>
    [Fact]
    public void ワークスペース外は絶対パスのまま()
        => Assert.Equal(@"E:\elsewhere\x.cs", WorkspacePaths.ToDisplayPath(Both, @"E:\elsewhere\x.cs"));
}

/// <summary>
/// <see cref="IWorkspaceService"/> の既定実装が <see cref="WorkspacePaths"/> と同じ答えを返すこと。
/// テスト用の実装も含め、どの実装を通しても振る舞いが1つであることを固定する
/// （個別実装に戻ると、また機能ごとにマルチルートの取りこぼしが再発する）。
/// </summary>
public sealed class WorkspaceServiceQueriesTests
{
    private sealed class Stub : IWorkspaceService
    {
        public IReadOnlyList<string> Folders { get; init; } = [];
        public string? RootPath => Folders.Count > 0 ? Folders[0] : null;
        public string? SelectedPath { get; set; }
        public void OpenFolder(string rootPath) { }
        public void AddFolder(string path) { }
        public void RemoveFolder(string path) { }
        public System.Threading.Tasks.Task<IReadOnlyList<sk0ya.Loomo.Core.Models.FileNode>> ListAsync(
            string path, System.Threading.CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<sk0ya.Loomo.Core.Models.FileNode>>([]);
        public System.Threading.Tasks.Task<string> ReadFileAsync(
            string path, System.Threading.CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult("");
        public string ResolvePath(string path) => Path.GetFullPath(path);
        public event EventHandler<string?>? SelectionChanged;
        public event EventHandler<string?>? RootChanged;
        public event EventHandler? FoldersChanged;
        internal void Unused() { SelectionChanged?.Invoke(this, null); RootChanged?.Invoke(this, null); FoldersChanged?.Invoke(this, EventArgs.Empty); }
    }

    private static IWorkspaceService Workspace(params string[] folders) => new Stub { Folders = folders };

    [Fact]
    public void Contains_は全フォルダーを見る()
    {
        var workspace = Workspace(@"C:\work\app", @"D:\shared\lib");

        Assert.True(workspace.Contains(@"D:\shared\lib\util.cs"));
        Assert.False(workspace.Contains(@"C:\work\app2\a.cs"));
    }

    [Fact]
    public void FolderFor_は担当フォルダーを返す()
        => Assert.Equal(@"D:\shared\lib",
            Workspace(@"C:\work\app", @"D:\shared\lib").FolderFor(@"D:\shared\lib\util.cs"));

    [Fact]
    public void ToDisplayPath_はフォルダー数で表記が変わる()
    {
        Assert.Equal("src/a.cs", Workspace(@"C:\work\app").ToDisplayPath(@"C:\work\app\src\a.cs"));
        Assert.Equal("app/src/a.cs",
            Workspace(@"C:\work\app", @"D:\shared\lib").ToDisplayPath(@"C:\work\app\src\a.cs"));
    }
}
