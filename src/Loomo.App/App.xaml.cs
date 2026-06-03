using System;
using System.Windows;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Observability;
using sk0ya.Loomo.Core.Safety;
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

        // 保存済みカラーテーマ・アクセントカラーを適用する
        _host.Services.GetRequiredService<ThemeManager>().Apply(settings.Theme, settings.AccentColor);

        var shell = _host.Services.GetRequiredService<ShellWindow>();
        shell.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // --- 設定 ---
        services.AddSingleton<AiSettings>();
        services.AddSingleton<AiSettingsStore>();
        services.AddHttpClient("ai", c => c.Timeout = TimeSpan.FromMinutes(5));

        // --- 安全設計（§10）：AiSettings が保持する SafetySettings を共有 ---
        services.AddSingleton(sp => sp.GetRequiredService<AiSettings>().Safety);
        services.AddSingleton<ISafetyPolicy, SafetyPolicy>();

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
        // コンテキスト長管理：現在プロバイダの上限に合わせ送信前に履歴をトリム
        services.AddSingleton<IContextWindowPolicy, SettingsContextWindowPolicy>();
        // Ollama から利用可能モデル一覧を取得（設定画面の選択肢）
        services.AddSingleton(sp => new ModelCatalogService(
            sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient("ai"),
            sp.GetRequiredService<AiSettings>()));

        // --- ツール ---
        services.AddSingleton<IAgentTool, ListDirectoryTool>();
        services.AddSingleton<IAgentTool, ReadFileTool>();
        services.AddSingleton<IAgentTool, FindFilesTool>();
        services.AddSingleton<IAgentTool, SearchFilesTool>();
        services.AddSingleton<IAgentTool, GetSelectionTool>();
        services.AddSingleton<IAgentTool, OpenInEditorTool>();
        services.AddSingleton<IAgentTool, ProposeEditTool>();
        services.AddSingleton<IAgentTool, ReplaceTextOnceTool>();
        services.AddSingleton<IAgentTool, ReplaceRangeTool>();
        services.AddSingleton<IAgentTool, GetSelectionTextTool>();
        services.AddSingleton<IAgentTool, RunCommandTool>();
        services.AddSingleton<ToolRegistry>();

        // --- 観測性（§20）：AI操作トレースを JSONL に記録。設定で無効化（オプトアウト）可。 ---
        // ファクトリは設定ロード後（ShellWindow 解決時）に実行されるため EnableTracing を反映できる。
        services.AddSingleton<ITraceSink>(sp =>
        {
            var obs = sp.GetRequiredService<AiSettings>().Observability;
            return obs.EnableTracing
                ? new JsonlTraceSink(maxSessions: obs.MaxSessions)
                : NullTraceSink.Instance;
        });

        // --- エージェント ---
        services.AddSingleton<AgentOrchestrator>();
        services.AddSingleton<ConversationStore>();

        // --- ログ解析・AI改善提案（観測性 Phase B）---
        services.AddSingleton<TraceReader>();
        services.AddSingleton<ImprovementAdvisor>();

        // --- ViewModels / Window ---
        services.AddSingleton<ThemeManager>();
        services.AddSingleton<WorkspaceStateStore>();
        services.AddSingleton<PromptHistoryStore>();
        services.AddSingleton<TabIconService>();
        services.AddSingleton<WorkspaceListViewModel>();
        services.AddSingleton<FolderTreeViewModel>();
        services.AddSingleton<AiBarViewModel>();
        services.AddSingleton<TabsViewModel>();
        services.AddSingleton<SessionsViewModel>();
        services.AddSingleton<AnalysisViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AppearanceViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<ShellWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}
