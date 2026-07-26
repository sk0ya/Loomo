using System;
using System.IO;
using Editor.Core.Lsp;
using Microsoft.Extensions.DependencyInjection;
using sk0ya.Loomo.App.DependencyInjection;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Services.Lsp;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// **本番の配線**（<c>AddLoomoLsp</c>）そのものを検証する。設計書 §30.7 の指摘どおり、以前は
/// テストが常にレジストリを注入していたため、設定画面・エディタ・セッションが別インスタンスを
/// 見ているという肝心の不具合だけが検証対象外だった。ここではコンテナから解決した実物を突き合わせる。
/// </summary>
public sealed class LspWiringTests : IDisposable
{
    private readonly string _dir;
    private readonly ServiceProvider _provider;

    public LspWiringTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "loomo-lsp-wiring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceService>(new FakeWorkspaceService());
        services.AddSingleton<ITerminalService>(new FakeTerminalService());
        LoomoServiceCollectionExtensions.AddLoomoLsp(services, Path.Combine(_dir, "lsp-servers.json"));
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void SettingsUiAndExCommandsAndSessionShareOneTable()
    {
        var table = _provider.GetRequiredService<LspServerTable>();
        var admin = _provider.GetRequiredService<ILspServerAdmin>();

        Assert.Same(table, admin);
    }

    [Fact]
    public void AddingFromTheSettingsUi_IsVisibleToTheExCommandsAndTheSession()
    {
        var settings = _provider.GetRequiredService<LspManagementService>();
        var admin = _provider.GetRequiredService<ILspServerAdmin>();   // エディタへ渡るのと同じもの
        var session = _provider.GetRequiredService<ILspWorkspace>();

        settings.AddOrUpdate(".zig", "zls", ["--stdio"]);

        Assert.Equal("zls", admin.GetForExtension(".zig")!.Executable);
        Assert.True(session.IsServerAvailableFor(".zig"));
    }

    [Fact]
    public void AddingFromTheExCommands_IsVisibleToTheSettingsUi()
    {
        var admin = _provider.GetRequiredService<ILspServerAdmin>();
        var settings = _provider.GetRequiredService<LspManagementService>();

        admin.Set(".zig", new LspServerDef("zls", [], "zig"));

        Assert.Contains(settings.GetRows(), r => r.Extension == ".zig" && r.Executable == "zls");
    }

    [Fact]
    public void ExCommandChanges_LandInLoomosOwnJsonFile()
    {
        var admin = _provider.GetRequiredService<ILspServerAdmin>();
        admin.Set(".zig", new LspServerDef("zls", [], "zig"));

        var store = Path.Combine(_dir, "lsp-servers.json");
        Assert.True(File.Exists(store));
        Assert.Contains("zls", File.ReadAllText(store));
    }

    [Fact]
    public void SessionIsRegisteredAsASingleInstanceUnderBothItsTypes()
    {
        Assert.Same(
            _provider.GetRequiredService<LspWorkspaceService>(),
            _provider.GetRequiredService<ILspWorkspace>());
    }
}
