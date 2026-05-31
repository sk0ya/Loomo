using System;
using System.Windows;
using AgentStudio.Ai;
using AgentStudio.App.Services;
using AgentStudio.App.ViewModels;
using AgentStudio.App.Views;
using AgentStudio.Core.Abstractions;
using AgentStudio.Core.Agent;
using AgentStudio.Core.Tools;
using AgentStudio.Core.Tools.Implementations;
using AgentStudio.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentStudio.App;

public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(ConfigureServices)
            .Build();

        var shell = _host.Services.GetRequiredService<ShellWindow>();
        shell.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // --- 設定 ---
        services.AddSingleton<AiSettings>();
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

        // --- ViewModels / Window ---
        services.AddSingleton<FolderTreeViewModel>();
        services.AddSingleton<AiBarViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<ShellWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}
