using System;
using System.IO;
using System.Linq;
using Editor.Core.Lsp;
using sk0ya.Loomo.Services.Lsp;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// Loomo の LSP 管理（カタログ・PATH 検出・促し判定・追加削除）の検証。
/// 対応表そのものの挙動は <see cref="LspServerTableTests"/>、本番の配線は <see cref="LspWiringTests"/>。
/// </summary>
public sealed class LspManagementTests : IDisposable
{
    private readonly string _storePath;
    private readonly LspServerTable _table;

    public LspManagementTests()
    {
        _storePath = Path.Combine(Path.GetTempPath(), "loomo-lsp-test-" + Guid.NewGuid().ToString("N") + ".json");
        _table = new LspServerTable(_storePath);
    }

    public void Dispose()
    {
        try { File.Delete(_storePath); } catch { }
    }

    private LspManagementService Service() => new(new FakeTerminalService(), _table);

    // ── カタログ ──────────────────────────────────────────────────────────

    [Fact]
    public void Catalog_CSharp_UsesRoslynStdioWithTelemetryOff()
    {
        var info = LspServerCatalog.ByExtension(".cs").Single();
        Assert.NotNull(info);
        Assert.Contains(".cs", info!.Extensions);
        Assert.Contains("--stdio", info.Args);
        Assert.Contains("--telemetryLevel", info.Args);
        Assert.False(string.IsNullOrWhiteSpace(info.InstallCommand));
        Assert.Contains("dotnet tool update --global roslyn-language-server", info.InstallCommand);
        Assert.Contains("dotnet tool install --global roslyn-language-server", info.InstallCommand);
        Assert.Contains(LspServerCatalog.RoslynVersion, info.InstallCommand);
    }

    [Fact]
    public void RoslynCSharp_AppearsAsBuiltInAndResetDoesNotRestoreCsharpLs()
    {
        var svc = Service();

        var initial = svc.GetRows().Single(r => r.Extension == ".cs");
        Assert.Equal(LspServerOrigin.BuiltIn, initial.Origin);
        Assert.Contains("Roslyn", initial.DisplayName);

        Assert.True(svc.Remove(".cs"));
        var removed = svc.GetRows().Single(r => r.Extension == ".cs");
        Assert.Equal(LspServerOrigin.Removed, removed.Origin);
        Assert.Contains("Roslyn", removed.DisplayName);
        Assert.Null(_table.GetForExtension(".cs"));

        Assert.True(svc.Reset(".cs"));
        var restored = _table.GetForExtension(".cs")!;
        Assert.True(LspServerCatalog.IsRoslynCSharp(".cs", restored));
        Assert.DoesNotContain("csharp-ls", restored.Executable, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Catalog_ByExtension_MatchesTypeScript()
    {
        var info = LspServerCatalog.ByExtension(".ts").FirstOrDefault();
        Assert.NotNull(info);
        Assert.Equal("typescript-language-server", info!.Executable);
    }

    /// <summary>
    /// カタログの自己矛盾（案内どおり入れても「未導入」のまま）の回帰。実行ファイル名は
    /// **インストールコマンドが PATH 上に作る名前**でなければならない。C# はここが
    /// ツールストア深部のフルパスになっていて、<c>ExecutableResolver</c> が永久に見つけられなかった。
    /// </summary>
    [Fact]
    public void カタログの実行ファイル名はPATHで解決できる素の名前である()
    {
        Assert.All(LspServerCatalog.Servers, info =>
            Assert.Equal(-1, info.Executable.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])));
    }

    [Fact]
    public void CSharpの既定はインストール案内が作るシム名を指す()
    {
        var info = LspServerCatalog.ByExtension(".cs").Single();

        // dotnet グローバルツールは %USERPROFILE%\.dotnet\tools\<ツール名>.cmd を作る（PATH 上）。
        Assert.Equal("roslyn-language-server", info.Executable);
        Assert.Contains($"dotnet tool update --global {info.Executable}", info.InstallCommand);
        Assert.Contains($"dotnet tool install --global {info.Executable}", info.InstallCommand);
    }

    /// <summary>促し対象なのにカタログ候補が無く「設定で追加できます」しか出せなかった拡張子の穴（§30.16.4）。</summary>
    [Theory]
    [InlineData(".mts", "typescript-language-server", "typescript")]
    [InlineData(".cts", "typescript-language-server", "typescript")]
    [InlineData(".mjs", "typescript-language-server", "javascript")]
    [InlineData(".cjs", "typescript-language-server", "javascript")]
    [InlineData(".svelte", "svelteserver", "svelte")]
    public void カタログはESM_CJSとSvelteも受け持つ(string ext, string executable, string languageId)
    {
        var info = LspServerCatalog.ByExtension(ext).Single();

        Assert.Equal(executable, info.Executable);
        Assert.Equal(languageId, info.LanguageIdFor(ext));
        Assert.False(string.IsNullOrWhiteSpace(info.InstallCommand));
        Assert.False(string.IsNullOrWhiteSpace(info.DocsUrl));
    }

    [Fact]
    public void ResolveServerFor_割り当てられた実行ファイルと表示名を返す()
    {
        var svc = Service();

        var resolved = svc.ResolveServerFor(".cs");
        Assert.NotNull(resolved);
        Assert.Equal("roslyn-language-server", resolved!.Value.Executable);
        Assert.Contains("Roslyn", resolved.Value.DisplayName);

        svc.Remove(".cs");
        Assert.Null(svc.ResolveServerFor(".cs"));   // 無効化した拡張子には担当サーバーが居ない
        Assert.Null(svc.ResolveServerFor(""));
    }

    // ── PATH 検出 ─────────────────────────────────────────────────────────

    [Fact]
    public void ExecutableResolver_UnknownExecutable_NotFound()
        => Assert.False(ExecutableResolver.IsOnPath("loomo-no-such-server-xyz"));

    [Fact]
    public void ExecutableResolver_FindsCmd()
        => Assert.True(ExecutableResolver.IsOnPath("cmd"));   // System32\cmd.exe は常に PATH 上

    // ── 追加 / 削除 / 復帰 ────────────────────────────────────────────────

    [Fact]
    public void AddOrUpdate_AppearsAsCustomRow()
    {
        var svc = Service();
        svc.AddOrUpdate(".zig", "zls", ["--stdio"]);

        var row = svc.GetRows().FirstOrDefault(r => r.Extension == ".zig");
        Assert.NotNull(row);
        Assert.Equal("zls", row!.Executable);
        Assert.Equal(LspServerOrigin.Custom, row.Origin);
    }

    [Fact]
    public void Remove_BuiltIn_HidesIt()
    {
        var svc = Service();
        Assert.True(svc.Remove(".cs"));
        var row = svc.GetRows().First(r => r.Extension == ".cs");
        Assert.Equal(LspServerOrigin.Removed, row.Origin);
    }

    [Fact]
    public void Reset_RestoresBuiltIn()
    {
        var svc = Service();
        svc.Remove(".cs");
        Assert.True(svc.Reset(".cs"));
        Assert.Equal(LspServerOrigin.BuiltIn, svc.GetRows().First(r => r.Extension == ".cs").Origin);
    }

    // ── 促し判定 ──────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_NoExtension_ReturnsNull()
        => Assert.Null(Service().EvaluateForFile("Makefile"));

    [Fact]
    public void Evaluate_UnknownSourceExtension_NotConfigured()
    {
        // カタログにも対応表にも無いが、言語サーバーが在りうるソース拡張子は促す。
        var info = Service().EvaluateForFile("Main.java");
        Assert.NotNull(info);
        Assert.Equal(LspPromptKind.NotConfigured, info!.Kind);
        Assert.Null(info.InstallCommand);
    }

    [Theory]
    [InlineData("logo.png")]      // 画像：言語サーバーは存在しえない
    [InlineData("photo.JPG")]     // 大文字小文字は無視
    [InlineData("archive.zip")]
    [InlineData("app.exe")]
    [InlineData("notes.zzz")]     // 素性の判らない拡張子も促さない
    [InlineData("readme.txt")]
    public void Evaluate_NonSourceExtension_ReturnsNull(string path)
        => Assert.Null(Service().EvaluateForFile(path));

    [Fact]
    public void Evaluate_MappedButNotInstalled_PromptsInstall()
    {
        var svc = Service();
        svc.AddOrUpdate(".foo", "loomo-no-such-server-xyz", []);
        var info = svc.EvaluateForFile("a.foo");
        Assert.NotNull(info);
        Assert.Equal(LspPromptKind.NotInstalled, info!.Kind);
    }

    [Fact]
    public void Evaluate_InstalledServer_NoPrompt()
    {
        var svc = Service();
        svc.AddOrUpdate(".foo", "cmd", []);   // cmd は PATH 上 → 導入済み扱い
        Assert.Null(svc.EvaluateForFile("a.foo"));
    }

    [Fact]
    public void InstallForPrompt_NoVisibleTerminal_ReturnsFalse()
    {
        var svc = Service();
        var info = svc.EvaluateForFile("Main.java")!;   // NotConfigured（InstallCommand なし）
        Assert.False(svc.InstallForPrompt(info));
    }
}
