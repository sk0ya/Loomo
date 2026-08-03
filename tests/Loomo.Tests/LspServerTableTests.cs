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

        Assert.Equal("roslyn-language-server", table.GetForExtension(".cs")!.Executable);
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
    public void Load_DropsTheLegacyRoslynToolStorePath()
    {
        // 旧組み込み（版を含むツールストア深部のフルパス）。PATH 上に無く、ツール更新で消える綴り。
        var legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet", "tools", ".store", "roslyn-language-server", "5.9.0-1.26303.1",
            "roslyn-language-server.win-x64", "5.9.0-1.26303.1", "tools", "net10.0", "win-x64",
            "Microsoft.CodeAnalysis.LanguageServer.exe");
        WriteStore(JsonSerializer.Serialize(new
        {
            Overrides = new Dictionary<string, object>
            {
                [".cs"] = new { Executable = legacy, Args = new[] { "--stdio" }, LanguageId = "csharp" },
            },
            Removed = Array.Empty<string>(),
        }));

        var table = new LspServerTable(StorePath);

        Assert.Equal("roslyn-language-server", table.GetForExtension(".cs")!.Executable);
        Assert.Equal(LspServerOrigin.BuiltIn, table.List().Single(e => e.Extension == ".cs").Origin);
    }

    [Fact]
    public void Load_引数を変えたシム名の上書きは残す()
    {
        // 既定がシム名（roslyn-language-server）になった以上、「シム名＋独自の引数」は正当な上書き。
        // 実行ファイル名だけで畳むと、設定 UI から登録できてそのセッションでは効くのに次回起動で
        // 黙って消える＝ユーザー設定のサイレントロスになる（レビュー指摘 R4）。
        WriteStore(JsonSerializer.Serialize(new
        {
            Overrides = new Dictionary<string, object>
            {
                [".cs"] = new
                {
                    Executable = "roslyn-language-server",
                    Args = new[] { "--stdio", "--logLevel", "Trace" },
                    LanguageId = "csharp",
                },
            },
            Removed = Array.Empty<string>(),
        }));

        var table = new LspServerTable(StorePath);

        var def = table.GetForExtension(".cs")!;
        Assert.Equal("roslyn-language-server", def.Executable);
        Assert.Contains("Trace", def.Args);
        Assert.Equal(LspServerOrigin.Custom, table.List().Single(e => e.Extension == ".cs").Origin);
    }

    [Fact]
    public void Load_組み込みと同一の上書きは畳んで組み込みへ戻す()
    {
        // 引数まで既定と同じなら上書きとして抱える意味が無い（組み込みの更新が届かなくなるだけ）。
        WriteStore(JsonSerializer.Serialize(new
        {
            Overrides = new Dictionary<string, object>
            {
                [".cs"] = new
                {
                    Executable = "roslyn-language-server",
                    Args = new[] { "--stdio", "--autoLoadProjects", "--telemetryLevel", "off" },
                    LanguageId = "csharp",
                },
            },
            Removed = Array.Empty<string>(),
        }));

        var table = new LspServerTable(StorePath);

        Assert.Equal(LspServerOrigin.BuiltIn, table.List().Single(e => e.Extension == ".cs").Origin);
    }

    // ── 破壊的操作のバックアップ（§30.16.3） ───────────────────────────────

    [Fact]
    public void Reset_直前の割り当てをバックアップへ残す()
    {
        var table = new LspServerTable(StorePath);
        table.Set(".cs", new LspServerDef("my-working-server", ["--stdio"], "csharp"));

        Assert.True(table.Reset(".cs"));   // 「動いていた割り当て」が消える操作

        var backup = LspServerTable.BackupPathFor(StorePath);
        Assert.True(File.Exists(backup), "リセット前の内容が復旧可能な形で残っていない");
        Assert.Contains("my-working-server", File.ReadAllText(backup));
        // 本体からは消えている（バックアップは本体の代わりではない）。
        Assert.DoesNotContain("my-working-server", File.ReadAllText(StorePath));
    }

    [Fact]
    public void Remove_も直前の内容をバックアップへ残す()
    {
        var table = new LspServerTable(StorePath);
        table.Set(".zig", new LspServerDef("zls", ["--stdio"], "zig"));

        Assert.True(table.Remove(".zig"));

        Assert.Contains("zls", File.ReadAllText(LspServerTable.BackupPathFor(StorePath)));
    }

    [Fact]
    public void BackupPathFor_保存先の隣に置く()
        => Assert.Equal(
            Path.Combine(_dir, "lsp-servers.backup.json"),
            LspServerTable.BackupPathFor(StorePath));

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
