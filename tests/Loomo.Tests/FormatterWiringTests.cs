using System;
using System.IO;
using Editor.Core.Engine;
using Editor.Core.Formatting;
using Microsoft.Extensions.DependencyInjection;
using sk0ya.Loomo.App.DependencyInjection;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Services.Formatting;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>設定画面とエディタが同じ整形レジストリを見るという、本番DIの所有境界を検証する。</summary>
public sealed class FormatterWiringTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "loomo-fmt-wiring-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Production_wiring_shares_one_registry_with_editor_engine_and_management()
    {
        Directory.CreateDirectory(_dir);
        var services = new ServiceCollection();
        services.AddSingleton<ITerminalService>(new FakeTerminalService());
        LoomoServiceCollectionExtensions.AddLoomoFormatting(
            services, Path.Combine(_dir, "formatters.json"));

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<FormatterRegistry>();
        var engineServices = provider.GetRequiredService<VimEngineServices>();
        var management = provider.GetRequiredService<FormatterManagementService>();

        Assert.Same(registry, engineServices.Formatters);
        management.AddOrUpdate(".loomo", "loomo-format", []);
        Assert.Equal("loomo-format", engineServices.Formatters.GetForExtension(".loomo")?.Executable);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
