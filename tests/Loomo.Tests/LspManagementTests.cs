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
        Assert.EndsWith(
            "Microsoft.CodeAnalysis.LanguageServer.exe",
            info.Executable,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            $"{Path.DirectorySeparatorChar}.dotnet{Path.DirectorySeparatorChar}tools{Path.DirectorySeparatorChar}.store{Path.DirectorySeparatorChar}",
            info.Executable,
            StringComparison.OrdinalIgnoreCase);
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
