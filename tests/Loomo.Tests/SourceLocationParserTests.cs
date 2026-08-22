using System;
using System.IO;
using System.Linq;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.Tests;

/// <summary>選択テキスト → ファイルの場所（パス＋行・列）の読み取りと解決。
/// ターミナル／エディタの右クリック「エディタへ送る」の土台。</summary>
public class SourceLocationParserTests
{
    // ── 書式の読み取り（純粋関数。ファイルシステムに触れない）────────────────────

    [Fact]
    public void 素のパスは行も列も0()
    {
        Assert.True(SourceLocationParser.TryParse("src/Program.cs", out var location));
        Assert.Equal("src/Program.cs", location.Path);
        Assert.Equal(0, location.Line);
        Assert.Equal(0, location.Column);
    }

    [Theory]
    [InlineData("src/Program.cs:12", "src/Program.cs", 12, 0)]
    [InlineData("src/Program.cs:12:5", "src/Program.cs", 12, 5)]
    // grep / rg の出力は行のあとに本文が続く。
    [InlineData("src/Program.cs:12:    var x = 1;", "src/Program.cs", 12, 0)]
    // 末尾に診断テキストが続く形（: の後が数字でなくなった時点で打ち切る）。
    [InlineData("src/foo.rs:12:5: error: cannot find value `x`", "src/foo.rs", 12, 5)]
    public void コロン区切りの行と列(string text, string path, int line, int column)
    {
        Assert.True(SourceLocationParser.TryParse(text, out var location));
        Assert.Equal(path, location.Path);
        Assert.Equal(line, location.Line);
        Assert.Equal(column, location.Column);
    }

    [Theory]
    [InlineData(@"src/Foo.cs(12,5): error CS0103: 名前 'x' は存在しません", "src/Foo.cs", 12, 5)]
    [InlineData(@"src/Foo.cs(12): warning CS0168: 変数が宣言されていますが未使用です", "src/Foo.cs", 12, 0)]
    [InlineData(@"C:\work\app\src\Foo.cs(3,7): error CS1002: ; が必要です", @"C:\work\app\src\Foo.cs", 3, 7)]
    public void MSBuildとCSharpコンパイラの括弧形式(string text, string path, int line, int column)
    {
        Assert.True(SourceLocationParser.TryParse(text, out var location));
        Assert.Equal(path, location.Path);
        Assert.Equal(line, location.Line);
        Assert.Equal(column, location.Column);
    }

    [Theory]
    [InlineData(@"File ""C:\work\app\main.py"", line 12", @"C:\work\app\main.py", 12)]
    [InlineData(@"  File ""C:\work\app\main.py"", line 12, in <module>", @"C:\work\app\main.py", 12)]
    [InlineData(@"File ""main.py""", "main.py", 0)]
    public void Pythonのトレースバック(string text, string path, int line)
    {
        Assert.True(SourceLocationParser.TryParse(text, out var location));
        Assert.Equal(path, location.Path);
        Assert.Equal(line, location.Line);
    }

    [Theory]
    [InlineData(@"at foo (C:\work\app\index.js:12:5)", @"C:\work\app\index.js", 12, 5)]
    [InlineData(@"    at Object.<anonymous> (C:\work\app\index.js:12:5)", @"C:\work\app\index.js", 12, 5)]
    [InlineData(@"at C:\work\app\index.js:12:5", @"C:\work\app\index.js", 12, 5)]
    [InlineData("at src/index.ts:3:1", "src/index.ts", 3, 1)]
    public void NodeとJSのスタックフレーム(string text, string path, int line, int column)
    {
        Assert.True(SourceLocationParser.TryParse(text, out var location));
        Assert.Equal(path, location.Path);
        Assert.Equal(line, location.Line);
        Assert.Equal(column, location.Column);
    }

    /// <summary>Windows のドライブレターを「: 区切りの行番号」と誤読しないこと
    /// （<c>C:\work\app.cs</c> の行番号が「\work\app.cs」になってはいけない）。</summary>
    [Theory]
    [InlineData(@"C:\work\app\Program.cs", @"C:\work\app\Program.cs", 0, 0)]
    [InlineData(@"C:/work/app/Program.cs", @"C:/work/app/Program.cs", 0, 0)]
    [InlineData(@"C:\work\app\Program.cs:12", @"C:\work\app\Program.cs", 12, 0)]
    [InlineData(@"C:\work\app\Program.cs:12:5", @"C:\work\app\Program.cs", 12, 5)]
    [InlineData(@"d:\work\app\Program.cs:7", @"d:\work\app\Program.cs", 7, 0)]
    public void ドライブレターは行番号ではない(string text, string path, int line, int column)
    {
        Assert.True(SourceLocationParser.TryParse(text, out var location));
        Assert.Equal(path, location.Path);
        Assert.Equal(line, location.Line);
        Assert.Equal(column, location.Column);
    }

    /// <summary>1 文字のファイル名はドライブ指定ではない（<c>:</c> の後ろが区切りでないので行番号）。</summary>
    [Fact]
    public void 一文字のファイル名はドライブ指定ではない()
    {
        Assert.True(SourceLocationParser.TryParse("a:12", out var location));
        Assert.Equal("a", location.Path);
        Assert.Equal(12, location.Line);
    }

    [Theory]
    [InlineData(@"""src/Program.cs:12""", "src/Program.cs", 12)]
    [InlineData("'src/Program.cs:12'", "src/Program.cs", 12)]
    [InlineData("`src/Program.cs:12`", "src/Program.cs", 12)]
    [InlineData("<src/Program.cs:12>", "src/Program.cs", 12)]
    [InlineData("(src/Program.cs:12)", "src/Program.cs", 12)]
    [InlineData("  src/Program.cs:12  ", "src/Program.cs", 12)]
    public void 引用符や囲みは外す(string text, string path, int line)
    {
        Assert.True(SourceLocationParser.TryParse(text, out var location));
        Assert.Equal(path, location.Path);
        Assert.Equal(line, location.Line);
    }

    [Theory]
    [InlineData("src/Program.cs:12,", "src/Program.cs", 12)]
    [InlineData("src/Program.cs:12。", "src/Program.cs", 12)]
    [InlineData("src/Program.cs:12;", "src/Program.cs", 12)]
    [InlineData("src/Program.cs:", "src/Program.cs", 0)]
    [InlineData("src/Program.cs.", "src/Program.cs", 0)]
    public void 末尾の句読点は落とす(string text, string path, int line)
    {
        Assert.True(SourceLocationParser.TryParse(text, out var location));
        Assert.Equal(path, location.Path);
        Assert.Equal(line, location.Line);
    }

    /// <summary>複数行を雑に選んだときは 1 行目（空行は読み飛ばす）だけを見る。</summary>
    [Theory]
    [InlineData("src/Program.cs:12\nsrc/Other.cs:99", "src/Program.cs", 12)]
    [InlineData("src/Program.cs:12\r\nsrc/Other.cs:99", "src/Program.cs", 12)]
    [InlineData("\n\n  src/Program.cs:12\nsrc/Other.cs:99", "src/Program.cs", 12)]
    public void 複数行は1行目だけ見る(string text, string path, int line)
    {
        Assert.True(SourceLocationParser.TryParse(text, out var location));
        Assert.Equal(path, location.Path);
        Assert.Equal(line, location.Line);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    [InlineData("...")]
    public void 読み取れないものは失敗(string? text)
        => Assert.False(SourceLocationParser.TryParse(text, out _));

    // ── Git の diff 接頭辞 ──────────────────────────────────────────────────

    [Theory]
    [InlineData("a/src/Foo.cs", "src/Foo.cs")]
    [InlineData("b/src/Foo.cs", "src/Foo.cs")]
    [InlineData("i/src/Foo.cs", "src/Foo.cs")]
    [InlineData("w/src/Foo.cs", "src/Foo.cs")]
    [InlineData("c/src/Foo.cs", "src/Foo.cs")]
    [InlineData("o/src/Foo.cs", "src/Foo.cs")]
    [InlineData(@"a\src\Foo.cs", @"src\Foo.cs")]
    public void Git接頭辞を剥がす(string path, string expected)
    {
        Assert.True(SourceLocationParser.TryStripGitPrefix(path, out var stripped));
        Assert.Equal(expected, stripped);
    }

    [Theory]
    [InlineData("ab/src/Foo.cs")]     // 2 文字は接頭辞ではない
    [InlineData("x/src/Foo.cs")]      // Git が使わない文字
    [InlineData("a/")]                // 剥がすと空
    [InlineData("src/Foo.cs")]
    [InlineData(null)]
    public void Git接頭辞でないものは剥がさない(string? path)
        => Assert.False(SourceLocationParser.TryStripGitPrefix(path, out _));

    // ── 解決（マルチルート）─────────────────────────────────────────────────

    [Fact]
    public void 解決_ターミナルのcwdを最優先する()
    {
        using var temp = new TempTree();
        var workspace = Workspace(temp, out var primary, out _);
        var cwd = Directory.CreateDirectory(Path.Combine(temp.Root, "cwd")).FullName;
        var expected = temp.Write("cwd", "src", "Program.cs", "// cwd");
        temp.Write("primary", "src", "Program.cs", "// primary");

        Assert.True(SourceLocationResolver.TryResolve(
            workspace, "src/Program.cs:12:5", cwd, currentDocumentPath: null, out var location));
        Assert.Equal(Path.GetFullPath(expected), location.Path);
        Assert.Equal(12, location.Line);
        Assert.Equal(5, location.Column);
        Assert.NotEqual(primary, Path.GetDirectoryName(Path.GetDirectoryName(location.Path)));
    }

    /// <summary>マルチルート: プライマリに無くても、追加フォルダーにあれば解決する
    /// （プライマリだけを基準にすると、追加フォルダーのビルド出力から飛べなくなる）。</summary>
    [Fact]
    public void 解決_追加フォルダーのファイルも見つける()
    {
        using var temp = new TempTree();
        var workspace = Workspace(temp, out _, out _);
        var expected = temp.Write("added", "src", "Only.cs", "// added");

        Assert.True(SourceLocationResolver.TryResolve(
            workspace, "src/Only.cs(3,1): error CS0103: x", workingDirectory: null,
            currentDocumentPath: null, out var location));
        Assert.Equal(Path.GetFullPath(expected), location.Path);
        Assert.Equal(3, location.Line);
        Assert.Equal(1, location.Column);
    }

    /// <summary>マルチルート: 相対パスの基準は「その文書を担当するフォルダー」。
    /// 同じ相対パスを両方に置いて、どちらを掴むかで判定する。</summary>
    [Fact]
    public void 解決_文書のあるフォルダーを基準にする()
    {
        using var temp = new TempTree();
        var workspace = Workspace(temp, out _, out _);
        var document = temp.Write("added", "docs", "README.md", "# docs");
        var decoy = temp.Write("primary", "src", "Program.cs", "// primary");
        var expected = temp.Write("added", "src", "Program.cs", "// added");

        Assert.True(SourceLocationResolver.TryResolve(
            workspace, "src/Program.cs:9", workingDirectory: null, document, out var location));
        Assert.Equal(Path.GetFullPath(expected), location.Path);
        Assert.NotEqual(Path.GetFullPath(decoy), location.Path);
        Assert.Equal(9, location.Line);
    }

    /// <summary>文書と同じフォルダーにある兄弟ファイルも解決する（エディタの選択でよくある形）。</summary>
    [Fact]
    public void 解決_文書と同じフォルダーの兄弟ファイル()
    {
        using var temp = new TempTree();
        var workspace = Workspace(temp, out _, out _);
        var document = temp.Write("primary", "src", "Program.cs", "// program");
        var expected = temp.Write("primary", "src", "Helper.cs", "// helper");

        Assert.True(SourceLocationResolver.TryResolve(
            workspace, "Helper.cs:4", workingDirectory: null, document, out var location));
        Assert.Equal(Path.GetFullPath(expected), location.Path);
    }

    [Fact]
    public void 解決_Git接頭辞を剥がして見つける()
    {
        using var temp = new TempTree();
        var workspace = Workspace(temp, out _, out _);
        var expected = temp.Write("primary", "src", "Foo.cs", "// foo");

        foreach (var prefix in new[] { "a/", "b/", "i/", "w/", "c/", "o/" })
        {
            Assert.True(SourceLocationResolver.TryResolve(
                workspace, prefix + "src/Foo.cs:7", workingDirectory: null,
                currentDocumentPath: null, out var location));
            Assert.Equal(Path.GetFullPath(expected), location.Path);
            Assert.Equal(7, location.Line);
        }
    }

    /// <summary>剥がす前のパスが実在するならそちらが勝つ（<c>a/</c> という実在フォルダーを壊さない）。</summary>
    [Fact]
    public void 解決_剥がす前のパスが実在すればそちらを優先()
    {
        using var temp = new TempTree();
        var workspace = Workspace(temp, out _, out _);
        var expected = temp.Write("primary", "a", "src", "Foo.cs", "// a/ 配下の実ファイル");
        var stripped = temp.Write("primary", "src", "Foo.cs", "// 剥がした先");

        Assert.True(SourceLocationResolver.TryResolve(
            workspace, "a/src/Foo.cs:2", workingDirectory: null,
            currentDocumentPath: null, out var location));
        Assert.Equal(Path.GetFullPath(expected), location.Path);
        Assert.NotEqual(Path.GetFullPath(stripped), location.Path);
    }

    /// <summary>括弧の中の数字がファイル名の一部のときは行番号と読まない
    /// （読めなかったときは素の 1 行へ倒す経路もある）。</summary>
    [Fact]
    public void 解決_括弧付きのファイル名を行番号と誤読しない()
    {
        using var temp = new TempTree();
        var workspace = Workspace(temp, out _, out _);
        var expected = temp.Write("primary", "notes(1).txt", "memo");

        Assert.True(SourceLocationResolver.TryResolve(
            workspace, "notes(1).txt", workingDirectory: null,
            currentDocumentPath: null, out var location));
        Assert.Equal(Path.GetFullPath(expected), location.Path);
        Assert.Equal(0, location.Line);
    }

    [Fact]
    public void 解決_絶対パスはそのまま()
    {
        using var temp = new TempTree();
        var workspace = Workspace(temp, out _, out _);
        var expected = temp.Write("outside", "Program.cs", "// outside");

        Assert.True(SourceLocationResolver.TryResolve(
            workspace, $"{expected}:31:2", workingDirectory: null,
            currentDocumentPath: null, out var location));
        Assert.Equal(Path.GetFullPath(expected), location.Path);
        Assert.Equal(31, location.Line);
        Assert.Equal(2, location.Column);
    }

    [Fact]
    public void 解決_実在しないものは失敗()
    {
        using var temp = new TempTree();
        var workspace = Workspace(temp, out _, out _);
        temp.Write("primary", "src", "Program.cs", "// program");

        Assert.False(SourceLocationResolver.TryResolve(
            workspace, "src/Missing.cs:12", workingDirectory: null,
            currentDocumentPath: null, out _));
        Assert.False(SourceLocationResolver.TryResolve(
            workspace, "ふつうの日本語の文章です。", workingDirectory: null,
            currentDocumentPath: null, out _));
    }

    /// <summary>フォルダーは「エディタへ送る」対象にしない（開いても何も起きない項目を作らないため）。</summary>
    [Fact]
    public void 解決_フォルダーは対象外()
    {
        using var temp = new TempTree();
        var workspace = Workspace(temp, out _, out _);
        Directory.CreateDirectory(Path.Combine(temp.Root, "primary", "src"));

        Assert.False(SourceLocationResolver.TryResolve(
            workspace, "src", workingDirectory: null, currentDocumentPath: null, out _));
    }

    /// <summary>プライマリ＋追加フォルダーの 2 フォルダーワークスペース。</summary>
    private static FakeWorkspaceService Workspace(TempTree temp, out string primary, out string added)
    {
        primary = Directory.CreateDirectory(Path.Combine(temp.Root, "primary")).FullName;
        added = Directory.CreateDirectory(Path.Combine(temp.Root, "added")).FullName;
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(primary);
        workspace.AddFolder(added);
        return workspace;
    }

    private sealed class TempTree : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "loomo-source-location-" + Guid.NewGuid().ToString("N"));

        /// <summary>末尾を内容、それ以外をルートからのパス要素として書き込む。</summary>
        public string Write(params string[] parts)
        {
            var path = Path.Combine(new[] { Root }.Concat(parts[..^1]).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, parts[^1]);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
