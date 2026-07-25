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
    public void EnsureCSharpDefault_ReplacesOnlyOldBuiltIn()
    {
        var registry = LspServerRegistry.Default;

        LspServerCatalog.EnsureCSharpDefault(registry);

        var row = registry.List().Single(e => e.Extension == ".cs");
        Assert.Equal(LspServerOrigin.Custom, row.Origin);
        Assert.EndsWith(
            "Microsoft.CodeAnalysis.LanguageServer.exe",
            row.Server.Executable,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--stdio", row.Server.Args);
    }

    [Fact]
    public void RoslynCSharp_AppearsAsBuiltInAndResetDoesNotRestoreCsharpLs()
    {
        var registry = LspServerRegistry.Default;
        LspServerCatalog.EnsureCSharpDefault(registry);
        var svc = Service();

        var initial = svc.GetRows().Single(r => r.Extension == ".cs");
        Assert.Equal(LspServerOrigin.BuiltIn, initial.Origin);
        Assert.Contains("Roslyn", initial.DisplayName);

        Assert.True(svc.Remove(".cs"));
        var removed = svc.GetRows().Single(r => r.Extension == ".cs");
        Assert.Equal(LspServerOrigin.Removed, removed.Origin);
        Assert.Contains("Roslyn", removed.DisplayName);
        Assert.Null(LspServerRegistry.Default.GetForExtension(".cs"));

        Assert.True(svc.Reset(".cs"));
        var restored = LspServerRegistry.Default.GetForExtension(".cs")!;
        Assert.True(LspServerCatalog.IsRoslynCSharp(".cs", restored));
        Assert.DoesNotContain("csharp-ls", restored.Executable, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureCSharpDefault_PreservesUserOverride()
    {
        var registry = LspServerRegistry.Default;
        registry.Set(".cs", new LspServerDef("my-csharp-server", ["serve"], "csharp"));

        LspServerCatalog.EnsureCSharpDefault(registry);

        Assert.Equal("my-csharp-server", registry.GetForExtension(".cs")!.Executable);
    }

    [Fact]
    public void EnsureCSharpDefault_ReplacesLegacyLoomoPrivateRoslynPath()
    {
        var registry = LspServerRegistry.Default;
        var oldDll = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Loomo", "lsp", "roslyn", "old",
            "Microsoft.CodeAnalysis.LanguageServer.dll");
        var oldDotnet = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Loomo", "lsp", "dotnet", "10.0.0", "dotnet.exe");
        registry.Set(".cs", new LspServerDef(oldDotnet, [oldDll, "--stdio"], "csharp"));

        LspServerCatalog.EnsureCSharpDefault(registry);

        Assert.True(LspServerCatalog.IsRoslynCSharp(
            ".cs", registry.GetForExtension(".cs")!));
    }

    [Fact]
    public void EnsureCSharpDefault_ReplacesExistingGlobalRoslynShim()
    {
        var registry = LspServerRegistry.Default;
        registry.Set(".cs", new LspServerDef(
            "roslyn-language-server", ["--stdio"], "csharp"));

        LspServerCatalog.EnsureCSharpDefault(registry);

        Assert.True(LspServerCatalog.IsRoslynCSharp(
            ".cs", registry.GetForExtension(".cs")!));
    }

    [Fact]
    public void EnsureCSharpDefault_PreservesDisabledBuiltIn()
    {
        var registry = LspServerRegistry.Default;
        registry.Remove(".cs");

        LspServerCatalog.EnsureCSharpDefault(registry);

        Assert.Null(registry.GetForExtension(".cs"));
        Assert.Equal(LspServerOrigin.Removed,
            registry.List().Single(e => e.Extension == ".cs").Origin);
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
