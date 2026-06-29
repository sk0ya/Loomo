using System;
using System.IO;
using System.Linq;
using Editor.Core.Formatting;
using sk0ya.Loomo.Services.Formatting;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// Loomo の整形フォーマッタ管理（カタログ・PATH 検出・適用/解除・カスタム追加削除）の検証。
/// エディタの共有レジストリ（FormatterRegistry.Default）はテスト毎に一時パスへ向け直して隔離する。
/// </summary>
public sealed class FormatterManagementTests : IDisposable
{
    private readonly string _storePath;

    public FormatterManagementTests()
    {
        _storePath = Path.Combine(Path.GetTempPath(), "loomo-fmt-test-" + Guid.NewGuid().ToString("N") + ".json");
        FormatterRegistry.ConfigureDefault(_storePath);
    }

    public void Dispose()
    {
        FormatterRegistry.ConfigureDefault(null);
        try { File.Delete(_storePath); } catch { }
    }

    private static FormatterManagementService Service() => new(new FakeTerminalService());

    // ── カタログ ──────────────────────────────────────────────────────────

    [Fact]
    public void Catalog_ByExecutable_FindsPrettier()
    {
        var info = FormatterCatalog.ByExecutable("prettier");
        Assert.NotNull(info);
        Assert.Contains(".ts", info!.Extensions);
        Assert.False(string.IsNullOrWhiteSpace(info.InstallCommand));
    }

    [Fact]
    public void Catalog_ByExtension_MatchesPython()
    {
        var execs = FormatterCatalog.ByExtension(".py").Select(f => f.Executable).ToList();
        Assert.Contains("black", execs);
        Assert.Contains("ruff", execs);
    }

    // ── 既定の一覧（カタログが土台） ──────────────────────────────────────

    [Fact]
    public void GetRows_EmptyRegistry_ListsCatalog()
    {
        var rows = Service().GetRows();
        Assert.Contains(rows, r => r.Executable == "prettier" && !r.IsCustom);
        // 何も割り当てていないので、どれも未適用。
        Assert.All(rows, r => Assert.False(r.Configured));
    }

    // ── 適用 / 解除 ───────────────────────────────────────────────────────

    [Fact]
    public void Apply_AssignsAllExtensions_ThenConfigured()
    {
        var svc = Service();
        var info = FormatterCatalog.ByExecutable("prettier")!;
        svc.Apply(info);

        var row = svc.GetRows().First(r => r.Executable == "prettier");
        Assert.True(row.Configured);
        Assert.Equal("prettier", FormatterRegistry.Default.GetForExtension(".ts")!.Executable);
    }

    [Fact]
    public void Unapply_RemovesAssignment()
    {
        var svc = Service();
        var info = FormatterCatalog.ByExecutable("prettier")!;
        svc.Apply(info);
        svc.Unapply(info);

        Assert.False(svc.GetRows().First(r => r.Executable == "prettier").Configured);
        Assert.Null(FormatterRegistry.Default.GetForExtension(".ts"));
    }

    [Fact]
    public void Unapply_LeavesOtherFormattersUntouched()
    {
        var svc = Service();
        // .md は prettier と dprint の両対応。dprint を当ててから prettier を解除しても dprint は残る。
        svc.Apply(FormatterCatalog.ByExecutable("dprint")!);
        svc.Unapply(FormatterCatalog.ByExecutable("prettier")!);

        Assert.Equal("dprint", FormatterRegistry.Default.GetForExtension(".md")!.Executable);
    }

    // ── カスタム追加 / 削除 ───────────────────────────────────────────────

    [Fact]
    public void AddOrUpdate_AppearsAsCustomRow()
    {
        var svc = Service();
        svc.AddOrUpdate(".cs", "csharpier", []);

        var row = svc.GetRows().FirstOrDefault(r => r.Key == ".cs" && r.IsCustom);
        Assert.NotNull(row);
        Assert.Equal("csharpier", row!.Executable);
        Assert.True(row.Configured);
    }

    [Fact]
    public void Remove_DeletesCustom()
    {
        var svc = Service();
        svc.AddOrUpdate(".cs", "csharpier", []);
        Assert.True(svc.Remove(".cs"));
        Assert.DoesNotContain(svc.GetRows(), r => r.Key == ".cs" && r.IsCustom);
    }

    // ── PATH 検出 / インストール ──────────────────────────────────────────

    [Fact]
    public void RunInstall_NoVisibleTerminal_ReturnsFalse()
        => Assert.False(Service().RunInstall("npm install -g prettier"));

    [Fact]
    public void IsInstalled_KnownSystemExe_True()
        => Assert.True(Service().IsInstalled("cmd"));   // System32\cmd.exe は常に PATH 上
}
