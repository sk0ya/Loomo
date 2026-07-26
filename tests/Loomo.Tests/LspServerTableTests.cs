using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Editor.Core.Lsp;
using sk0ya.Loomo.Services.Lsp;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 拡張子→言語サーバーの対応表（Loomo 所有）の検証。旧 <c>Editor.Core.Lsp.LspServerRegistry</c> からの移管。
/// </summary>
public sealed class LspServerTableTests : IDisposable
{
    private readonly string _dir;

    public LspServerTableTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "loomo-lsp-table-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string StorePath => Path.Combine(_dir, "lsp-servers.json");
    private static LspServerTable InMemory() => new(null);

    // ── 永続化先（§30.7） ──────────────────────────────────────────────────

    [Fact]
    public void DefaultStorePath_IsUnderLoomoAppData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Loomo", "lsp-servers.json");
        Assert.Equal(expected, LspServerTable.DefaultStorePath());
    }

    [Fact]
    public void Changes_RoundTripThroughTheJsonStore()
    {
        var table = new LspServerTable(StorePath);
        table.Set(".zig", new LspServerDef("zls", ["--stdio"], "zig"));
        table.Remove(".py");

        var reloaded = new LspServerTable(StorePath);

        Assert.Equal("zls", reloaded.GetForExtension(".zig")!.Executable);
        Assert.Null(reloaded.GetForExtension(".py"));
        // スキーマは旧 registry と同じ（移行処理を不要にするため）。
        using var doc = JsonDocument.Parse(File.ReadAllText(StorePath));
        Assert.True(doc.RootElement.TryGetProperty("Overrides", out _));
        Assert.True(doc.RootElement.TryGetProperty("Removed", out _));
    }

    // ── 組み込み既定はカタログから導出される ───────────────────────────────

    [Fact]
    public void Builtins_ComeFromTheCatalogWithPerExtensionLanguageIds()
    {
        var table = InMemory();

        Assert.EndsWith(
            "Microsoft.CodeAnalysis.LanguageServer.exe",
            table.GetForExtension(".cs")!.Executable, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("typescript-language-server", table.GetForExtension(".tsx")!.Executable);
        // 同じ実行ファイルでも拡張子ごとに languageId は違う。
        Assert.Equal("typescript", table.GetForExtension(".ts")!.LanguageId);
        Assert.Equal("typescriptreact", table.GetForExtension(".tsx")!.LanguageId);
        Assert.Equal("javascript", table.GetForExtension(".js")!.LanguageId);
        Assert.Equal("c", table.GetForExtension(".h")!.LanguageId);
        Assert.Equal("cpp", table.GetForExtension(".hpp")!.LanguageId);
    }

    [Fact]
    public void Builtins_CoverEveryCatalogTarget()
    {
        var expected = LspServerCatalog.Servers.SelectMany(s => s.Targets.Select(t => t.Extension));
        Assert.All(expected, ext => Assert.NotNull(InMemory().GetForExtension(ext)));
    }

    // ── 上書き・無効化・復帰 ───────────────────────────────────────────────

    [Fact]
    public void Set_WinsOverBuiltInAndShowsAsCustom()
    {
        var table = InMemory();
        table.Set(".cs", new LspServerDef("my-server", [], "csharp"));

        Assert.Equal("my-server", table.GetForExtension(".cs")!.Executable);
        Assert.Equal(LspServerOrigin.Custom, table.List().Single(e => e.Extension == ".cs").Origin);
    }

    [Fact]
    public void Remove_HidesABuiltIn_ResetRestoresIt()
    {
        var table = InMemory();

        Assert.True(table.Remove(".cs"));
        Assert.Null(table.GetForExtension(".cs"));
        Assert.Equal(LspServerOrigin.Removed, table.List().Single(e => e.Extension == ".cs").Origin);

        Assert.True(table.Reset(".cs"));
        Assert.NotNull(table.GetForExtension(".cs"));
        Assert.Equal(LspServerOrigin.BuiltIn, table.List().Single(e => e.Extension == ".cs").Origin);
    }

    [Fact]
    public void NormalizesBareAndUppercaseExtensions()
    {
        var table = InMemory();
        table.Set("ZIG", new LspServerDef("zls", [], "zig"));
        Assert.Equal("zls", table.GetForExtension(".zig")!.Executable);
    }

    [Fact]
    public void Changed_FiresSoTheSessionCanReopenInPlace()
    {
        var table = InMemory();
        var seen = new List<string>();
        table.Changed += seen.Add;

        table.Set(".zig", new LspServerDef("zls", [], "zig"));
        table.Remove(".zig");
        table.Reset(".nothing-here");   // 変化なし → 発火しない

        Assert.Equal([".zig", ".zig"], seen);
    }

    // ── 旧 C# サーバー設定の移行 ───────────────────────────────────────────

    [Theory]
    [InlineData("csharp-ls")]
    [InlineData("roslyn-language-server")]
    public void Load_DropsSupersededCSharpOverrides(string legacyExecutable)
    {
        WriteStore($$"""
            { "Overrides": { ".cs": { "Executable": "{{legacyExecutable}}", "Args": [], "LanguageId": "csharp" } },
              "Removed": [] }
            """);

        var table = new LspServerTable(StorePath);

        // 古いユーザー設定は捨てて、組み込み（Roslyn）へ戻る。
        Assert.True(LspServerCatalog.IsRoslynCSharp(".cs", table.GetForExtension(".cs")!));
        Assert.Equal(LspServerOrigin.BuiltIn, table.List().Single(e => e.Extension == ".cs").Origin);
    }

    [Fact]
    public void Load_DropsLegacyLoomoPrivateRoslynPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dll = Path.Combine(appData, "Loomo", "lsp", "roslyn", "old", "Microsoft.CodeAnalysis.LanguageServer.dll");
        var dotnet = Path.Combine(appData, "Loomo", "lsp", "dotnet", "10.0.0", "dotnet.exe");
        WriteStore(JsonSerializer.Serialize(new
        {
            Overrides = new Dictionary<string, object>
            {
                [".cs"] = new { Executable = dotnet, Args = new[] { dll, "--stdio" }, LanguageId = "csharp" },
            },
            Removed = Array.Empty<string>(),
        }));

        var table = new LspServerTable(StorePath);

        Assert.True(LspServerCatalog.IsRoslynCSharp(".cs", table.GetForExtension(".cs")!));
    }

    [Fact]
    public void Load_DropsAnOverrideThatMatchesTheBuiltIn()
    {
        // 組み込みへ昇格した割り当ての残骸。効果は無いのに「custom」表示になり、
        // 以後この拡張子だけ組み込みの更新が届かなくなる。
        var builtin = InMemory().GetForExtension(".cs")!;
        WriteStore(JsonSerializer.Serialize(new
        {
            Overrides = new Dictionary<string, object>
            {
                [".cs"] = new { builtin.Executable, builtin.Args, builtin.LanguageId },
            },
            Removed = Array.Empty<string>(),
        }));

        var table = new LspServerTable(StorePath);

        Assert.Equal(LspServerOrigin.BuiltIn, table.List().Single(e => e.Extension == ".cs").Origin);
        Assert.DoesNotContain("Overrides\": {\n    \".cs\"", File.ReadAllText(StorePath));
    }

    [Fact]
    public void Load_KeepsAGenuineUserOverride()
    {
        WriteStore("""
            { "Overrides": { ".cs": { "Executable": "my-csharp-server", "Args": ["serve"], "LanguageId": "csharp" } },
              "Removed": [] }
            """);

        Assert.Equal("my-csharp-server", new LspServerTable(StorePath).GetForExtension(".cs")!.Executable);
    }

    [Fact]
    public void Load_KeepsADisabledBuiltIn()
    {
        WriteStore("""{ "Overrides": {}, "Removed": [".cs"] }""");

        Assert.Null(new LspServerTable(StorePath).GetForExtension(".cs"));
    }

    [Fact]
    public void Load_CorruptStore_FallsBackToBuiltinsInsteadOfThrowing()
    {
        WriteStore("{ not json");

        Assert.NotNull(new LspServerTable(StorePath).GetForExtension(".cs"));
    }

    private void WriteStore(string json) => File.WriteAllText(StorePath, json);
}
