using System;
using System.Windows;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Tools;
using sk0ya.Loomo.Core.Tools.Implementations;
using sk0ya.Loomo.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace sk0ya.Loomo.App;

public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(ConfigureServices)
            .Build();

        // 保存済み設定（プロバイダ・APIキー等）を起動時に反映する
        var settings = _host.Services.GetRequiredService<AiSettings>();
        _host.Services.GetRequiredService<AiSettingsStore>().Load(settings);

        var shell = _host.Services.GetRequiredService<ShellWindow>();
        shell.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // --- 設定 ---
        services.AddSingleton<AiSettings>();
        services.AddSingleton<AiSettingsStore>();
        services.AddHttpClient("ai", c => c.Timeout = TimeSpan.FromMinutes(5));

        // --- サービス（concrete + interface 同一インスタンス） ---
        services.AddSingleton<WorkspaceService>();
        services.AddSingleton<IWorkspaceService>(sp => sp.GetRequiredService<WorkspaceService>());

        services.AddSingleton<TerminalService>();
        services.AddSingleton<ITerminalService>(sp => sp.GetRequiredService<TerminalService>());

        services.AddSingleton<EditorService>();
        services.AddSingleton<IEditorService>(sp => sp.GetRequiredService<EditorService>());

        services.AddSingleton<UiApprovalService>();
        services.AddSingleton<IApprovalService>(sp => sp.GetRequiredService<UiApprovalService>());

        // --- AI ---
        services.AddSingleton<IAiClientFactory, AiClientFactory>();
        services.AddSingleton(sp => new CopilotAuthService(
            sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient("ai")));

        // --- ツール ---
        services.AddSingleton<IAgentTool, ListDirectoryTool>();
        services.AddSingleton<IAgentTool, ReadFileTool>();
        services.AddSingleton<IAgentTool, GetSelectionTool>();
        services.AddSingleton<IAgentTool, OpenInEditorTool>();
        services.AddSingleton<IAgentTool, ProposeEditTool>();
        services.AddSingleton<IAgentTool, RunCommandTool>();
        services.AddSingleton<ToolRegistry>();

        // --- エージェント ---
        services.AddSingleton<AgentOrchestrator>();
        services.AddSingleton<ConversationStore>();

        // --- ViewModels / Window ---
        services.AddSingleton<FolderTreeViewModel>();
        services.AddSingleton<AiBarViewModel>();
        services.AddSingleton<SessionsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<ShellWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}
