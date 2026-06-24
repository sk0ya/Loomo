using System;
using System.IO;
using System.Linq;
using Editor.Core.Lsp;
using sk0ya.Loomo.Services.Lsp;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// Loomo の LSP 管理（カタログ・PATH 検出・促し判定・追加削除）の検証。
/// エディタの共有レジストリ（LspServerRegistry.Default）はテスト毎に一時パスへ向け直して隔離する。
/// </summary>
public sealed class LspManagementTests : IDisposable
{
    private readonly string _storePath;

    public LspManagementTests()
    {
        _storePath = Path.Combine(Path.GetTempPath(), "loomo-lsp-test-" + Guid.NewGuid().ToString("N") + ".json");
        LspServerRegistry.ConfigureDefault(_storePath);
    }

    public void Dispose()
    {
        LspServerRegistry.ConfigureDefault(null);
        try { File.Delete(_storePath); } catch { }
    }

    private static LspManagementService Service() => new(new FakeTerminalService());

    // ── カタログ ──────────────────────────────────────────────────────────

    [Fact]
    public void Catalog_ByExecutable_FindsKnownServer()
    {
        var info = LspServerCatalog.ByExecutable("csharp-ls");
        Assert.NotNull(info);
        Assert.Contains(".cs", info!.Extensions);
        Assert.False(string.IsNullOrWhiteSpace(info.InstallCommand));
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
    public void Evaluate_UnknownExtension_NotConfigured()
    {
        var info = Service().EvaluateForFile("notes.zzz");
        Assert.NotNull(info);
        Assert.Equal(LspPromptKind.NotConfigured, info!.Kind);
        Assert.Null(info.InstallCommand);
    }

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
        var info = svc.EvaluateForFile("notes.zzz")!;   // NotConfigured（InstallCommand なし）
        Assert.False(svc.InstallForPrompt(info));
    }
}
