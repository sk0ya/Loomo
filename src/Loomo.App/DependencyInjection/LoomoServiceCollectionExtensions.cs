using Microsoft.Extensions.DependencyInjection;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Ai.Clients;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Diff;
using sk0ya.Loomo.Core.Observability;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Core.Tools;
using sk0ya.Loomo.Core.Tools.Implementations;
using sk0ya.Loomo.Services;
using sk0ya.Loomo.Services.Search;

namespace sk0ya.Loomo.App.DependencyInjection;

internal static class LoomoServiceCollectionExtensions
{
    public static IServiceCollection AddLoomoCore(this IServiceCollection services)
    {
        services.AddLogging();

        // 実行中に変更される設定と、それを参照するポリシーはアプリ全体で同じ状態を共有する。
        services.AddSingleton<AiSettings>();
        services.AddSingleton<AiSettingsStore>();
        services.AddSingleton(sp => sp.GetRequiredService<AiSettings>().Safety);
        services.AddSingleton<ISafetyPolicy, SafetyPolicy>();

        AddAliasedSingleton<WorkspaceService, IWorkspaceService>(services);
        AddAliasedSingleton<WorkspaceSearchService, IWorkspaceSearchService>(services);
        AddAliasedSingleton<TerminalService, ITerminalService>(services);
        AddAliasedSingleton<EditorService, IEditorService>(services);
        AddAliasedSingleton<BrowserService, IBrowserService>(services);
        AddAliasedSingleton<UiApprovalService, IApprovalService>(services);

        services.AddSingleton<sk0ya.Loomo.Services.Lsp.LspManagementService>();
        services.AddSingleton<sk0ya.Loomo.Services.Formatting.FormatterManagementService>();
        services.AddSingleton<sk0ya.Loomo.Services.Debug.IDebugSessionFactory,
            sk0ya.Loomo.Services.Debug.NetcoredbgDebugSessionFactory>();
        // TS IDE ペイン用の js-debug 工場。IDebugSessionFactory の既定登録（netcoredbg）は dotnet 用 IDE ペインの
        // ものなので、こちらは具象型で登録して TsDebugViewModel だけが使う。
        services.AddSingleton<sk0ya.Loomo.Services.Debug.Js.JsDebugSessionFactory>();
        services.AddSingleton<ITestDiscoveryService,
            sk0ya.Loomo.Services.Debug.TestDiscoveryService>();

        // ツール、会話、トレースは一つのエージェント実行状態を共有するため Singleton。
        services.AddSingleton<IAgentTool, PwshTool>();
        services.AddSingleton<IAgentTool, WriteFileTool>();
        services.AddSingleton<IAgentTool, EditFileTool>();
        services.AddSingleton<IAgentTool, WebSearchTool>();
        services.AddSingleton<ToolRegistry>();
        services.AddSingleton<ISubAgentRunner, SubAgentRunner>();
        services.AddSingleton<ITraceSink>(sp =>
        {
            var obs = sp.GetRequiredService<AiSettings>().Observability;
            return obs.EnableTracing
                ? new JsonlTraceSink(maxSessions: obs.MaxSessions)
                : NullTraceSink.Instance;
        });
        AddAliasedSingleton<FileChangeJournal, IFileChangeJournal>(services);
        services.AddSingleton<AgentOrchestrator>();
        services.AddSingleton<ConversationStore>();
        services.AddSingleton<WorkflowStore>();
        services.AddSingleton<TraceReader>();

        return services;
    }

    public static IServiceCollection AddLoomoAi(this IServiceCollection services)
    {
        services.AddHttpClient("ai", c => c.Timeout = TimeSpan.FromMinutes(5));

        // 推論エンジンはモデルをメモリに常駐させ、全ターンで再利用するため Singleton。
        services.AddSingleton<OnnxGenAiEngine>();
        services.AddSingleton<LlamaCppEngine>();
        AddAliasedSingleton<LocalInferenceRouter, ILocalInferenceEngine>(services);
        services.AddSingleton<IAiClientFactory, AiClientFactory>();
        services.AddSingleton<IContextWindowPolicy, SettingsContextWindowPolicy>();
        services.AddSingleton(sp => new ModelCatalogService(sp.GetRequiredService<AiSettings>()));
        services.AddSingleton(sp => new ModelDownloadService(
            sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient("ai")));

        return services;
    }

    public static IServiceCollection AddLoomoGit(this IServiceCollection services)
    {
        // git CLI の監視状態と各画面の選択状態を共有するため、Git 機能は Singleton で構成する。
        services.AddSingleton<GitService>();
        services.AddSingleton<GitRootSwitchViewModel>();
        services.AddSingleton<GitPanelViewModel>();
        services.AddSingleton<GitSessionQuery>();
        services.AddSingleton<GitSessionCommandHandler>();
        services.AddSingleton<GitHistoryViewModel>();
        services.AddSingleton<GitSessionViewModel>();
        services.AddSingleton<DiffFileGateway>();
        services.AddSingleton<DiffSessionQuery>();
        services.AddSingleton<DiffSessionCommandHandler>();
        services.AddSingleton<DiffSessionViewModel>();

        return services;
    }

    public static IServiceCollection AddLoomoEditorSupport(this IServiceCollection services)
    {
        // プロバイダは不変の対応情報を持ち、Registry/Resolver と同じインスタンスを再利用する。
        services.AddSingleton<JsonSchemaValidator>();
        services.AddSingleton<IEditorSupportProvider, MarkdownEditorSupport>();
        services.AddSingleton<IEditorSupportProvider, JsonEditorSupport>();
        services.AddSingleton<IEditorSupportProvider, NdjsonEditorSupport>();
        services.AddSingleton<IEditorSupportProvider, YamlEditorSupport>();
        services.AddSingleton<IEditorSupportProvider, TomlEditorSupport>();
        services.AddSingleton<IEditorSupportProvider, XmlEditorSupport>();
        services.AddSingleton<IEditorSupportProvider, MermaidEditorSupport>();
        services.AddSingleton<IEditorSupportProvider, ImageEditorSupport>();
        services.AddSingleton<IEditorSupportProvider, VGridEditorSupport>();
        services.AddSingleton<IEditorSupportProvider, ExcelEditorSupport>();
        services.AddSingleton<IEditorSupportProvider, WordEditorSupport>();
        services.AddSingleton<IEditorSupportProvider, SqliteEditorSupport>();
        services.AddSingleton<IEditorSupportProvider, ParquetEditorSupport>();
        services.AddSingleton<IEditorSupportProvider, BrowserEditorSupport>();
        services.AddSingleton<IEditorSupportProvider, MediaEditorSupport>();
        services.AddSingleton<IEditorSupportProvider, FontEditorSupport>();
        services.AddSingleton<IEditorSupportProvider, LogEditorSupport>();
        services.AddSingleton<EditorSupportRegistry>();
        services.AddSingleton<EditorSupportResolver>();
        services.AddSingleton<IEditorSupportViewFactory, EditorSupportViewFactory>();
        services.AddSingleton<HexEditorSupport>();
        services.AddSingleton<CodeEditorSupport>();

        return services;
    }

    public static IServiceCollection AddLoomoPresentation(this IServiceCollection services)
    {
        // WPF 画面と ViewModel はウィンドウ存続期間と一致し、選択状態を共有するため Singleton。
        services.AddSingleton<ThemeManager>();
        services.AddSingleton<UiFontManager>();
        AddAliasedSingleton<LocalLlmWarmupService, IAiWarmup>(services);
        services.AddSingleton<AppBootstrapper>();
        services.AddSingleton<WorkspaceStateStore>();
        services.AddSingleton<PromptHistoryStore>();
        services.AddSingleton<ModelFolderGateway>();
        services.AddSingleton<ModelFolderPicker>();
        services.AddSingleton<BlockedCommandsHandler>();
        services.AddSingleton<SettingsPersistenceHandler>();
        services.AddSingleton<SettingsModelChoiceMapper>();
        services.AddSingleton<TabIconService>();
        services.AddSingleton<Input.KeybindingService>();
        services.AddSingleton<FolderTreeCommandHandler>();
        services.AddSingleton<FolderTreeQuery>();
        services.AddSingleton<WorkspaceListViewModel>();
        services.AddSingleton<FolderTreeViewModel>();
        services.AddSingleton<WorkflowToolRunner>();
        services.AddSingleton<WorkflowViewModel>();
        services.AddSingleton<AiBarViewModel>();
        services.AddSingleton<TabsViewModel>();
        services.AddSingleton<SessionsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AppearanceViewModel>();
        services.AddSingleton<LspSettingsViewModel>();
        services.AddSingleton<LspPromptViewModel>();
        services.AddSingleton<FormatterSettingsViewModel>();
        services.AddSingleton<KeybindingsViewModel>();
        services.AddSingleton<TraceSessionViewModel>();
        services.AddSingleton<PegboardViewModel>();
        services.AddSingleton<SearchResultTreeMapper>();
        services.AddSingleton<SearchPanelQuery>();
        services.AddSingleton<SearchPanelViewModel>();
        services.AddSingleton<sk0ya.Loomo.Core.Debug.DebugLaunchProfileStore>();
        services.AddSingleton<DebugViewModel>();
        // TS IDE のプロファイルは dotnet と別ファイル（tsLaunchProfiles.json）に保存する（レコード形は共用）。
        services.AddSingleton(sp => new TsDebugViewModel(
            sp.GetRequiredService<sk0ya.Loomo.Services.Debug.Js.JsDebugSessionFactory>(),
            sp.GetRequiredService<IWorkspaceService>(),
            sp.GetRequiredService<ITerminalService>(),
            new sk0ya.Loomo.Core.Debug.DebugLaunchProfileStore(System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "Loomo", "tsLaunchProfiles.json"))));
        services.AddSingleton<TrailStore>();
        services.AddSingleton<TrailViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<ShellWindow>();

        return services;
    }

    private static void AddAliasedSingleton<TImplementation, TService>(IServiceCollection services)
        where TImplementation : class, TService
        where TService : class
    {
        services.AddSingleton<TImplementation>();
        services.AddSingleton<TService>(sp => sp.GetRequiredService<TImplementation>());
    }
}
