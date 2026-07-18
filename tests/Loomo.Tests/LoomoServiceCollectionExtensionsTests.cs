using Microsoft.Extensions.DependencyInjection;
using sk0ya.Loomo.Ai.Clients;
using sk0ya.Loomo.App.DependencyInjection;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Tools;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

public sealed class LoomoServiceCollectionExtensionsTests
{
    [Fact]
    public void 全機能の登録グラフを検証できる()
    {
        var services = CreateAllServices();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    [Fact]
    public void 機能別入口が担当する代表サービスをSingletonで登録する()
    {
        AssertSingleton<AgentOrchestrator>(new ServiceCollection().AddLoomoCore());
        AssertSingleton<OnnxGenAiEngine>(new ServiceCollection().AddLoomoAi());
        AssertSingleton<GitService>(new ServiceCollection().AddLoomoGit());
        AssertSingleton<EditorSupportResolver>(new ServiceCollection().AddLoomoEditorSupport());
        AssertSingleton<ShellWindow>(new ServiceCollection().AddLoomoPresentation());
    }

    [Fact]
    public void 状態を共有するinterface登録はconcreteと同じSingletonを返す()
    {
        var services = CreateAllServices();
        using var provider = services.BuildServiceProvider();

        Assert.Same(
            provider.GetRequiredService<WorkspaceService>(),
            provider.GetRequiredService<IWorkspaceService>());
        Assert.Same(
            provider.GetRequiredService<LocalInferenceRouter>(),
            provider.GetRequiredService<ILocalInferenceEngine>());
        Assert.Same(
            provider.GetRequiredService<LocalLlmWarmupService>(),
            provider.GetRequiredService<IAiWarmup>());
    }

    [Fact]
    public void EditorSupportの追加登録先が一意に定まる()
    {
        var services = new ServiceCollection().AddLoomoEditorSupport();

        Assert.Equal(17, services.Count(x => x.ServiceType == typeof(IEditorSupportProvider)));
        Assert.DoesNotContain(
            new ServiceCollection().AddLoomoPresentation(),
            x => x.ServiceType == typeof(IEditorSupportProvider));
    }

    private static ServiceCollection CreateAllServices()
    {
        var services = new ServiceCollection();
        services
            .AddLoomoCore()
            .AddLoomoAi()
            .AddLoomoGit()
            .AddLoomoEditorSupport()
            .AddLoomoPresentation();
        return services;
    }

    private static void AssertSingleton<TService>(IServiceCollection services)
    {
        var descriptor = Assert.Single(services, x => x.ServiceType == typeof(TService));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }
}
