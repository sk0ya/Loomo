using Microsoft.Extensions.DependencyInjection;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Ai.Clients;
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
using sk0ya.Loomo.Services.Search;

namespace sk0ya.Loomo.App.DependencyInjection;

internal static class LoomoServiceCollectionExtensions
{
    public static IServiceCollection AddLoomoCore(this IServiceCollection services)
    {
        services.AddLogging();

        // 実行中に変更される設定と、それを参照するポリシーはアプリ全体で同じ状態を共有する。
        services.AddSingleton<LoomoSettings>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton(sp => sp.GetRequiredService<LoomoSettings>().Safety);
        services.AddSingleton<ISafetyPolicy, SafetyPolicy>();

        AddAliasedSingleton<WorkspaceService, IWorkspaceService>(services);
        AddAliasedSingleton<WorkspaceSearchService, IWorkspaceSearchService>(services);
        AddAliasedSingleton<TerminalService, ITerminalService>(services);
        AddAliasedSingleton<EditorService, IEditorService>(services);
        AddAliasedSingleton<BrowserService, IBrowserService>(services);
        AddAliasedSingleton<UiApprovalService, IApprovalService>(services);

        AddLoomoLsp(services);
        AddLoomoFormatting(services);
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
            var obs = sp.GetRequiredService<LoomoSettings>().Observability;
            return obs.EnableTracing
                ? new JsonlTraceSink(maxSessions: obs.MaxSessions)
                : NullTraceSink.Instance;
        });
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
        services.AddSingleton(sp => new ModelCatalogService(sp.GetRequiredService<LoomoSettings>()));
        services.AddSingleton(sp => new ModelDownloadService(
            sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient("ai")));

        return services;
    }

    public static IServiceCollection AddLoomoGit(this IServiceCollection services)
    {
        // git CLI の監視状態と各画面の選択状態を共有するため、Git 機能は Singleton で構成する。
        services.AddSingleton<GitService>();
        services.AddSingleton<GitRootSwitchViewModel>();
        // 比較基準は Git パネルと Diff ペインで共有する一つの状態なので Singleton。
        services.AddSingleton<GitCompareBaseViewModel>();
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
        services.AddSingleton<IEditorSupportProvider, PochiEditorSupport>();
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
        services.AddSingleton<IEditorSupportProvider, FlowEditorSupport>();
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
        services.AddSingleton<RecentUsageService>();
        services.AddSingleton<RecentItemsViewModel>();
        services.AddSingleton<PromptHistoryStore>();
        services.AddSingleton<ModelFolderGateway>();
        services.AddSingleton<ModelFolderPicker>();
        services.AddSingleton<BlockedCommandsHandler>();
        services.AddSingleton<SettingsPersistenceHandler>();
        services.AddSingleton<SettingsModelChoiceMapper>();
        services.AddSingleton<TabIconService>();
        services.AddSingleton<IFileThumbnailService, FileThumbnailService>();
        services.AddSingleton<Input.KeybindingService>();
        // ファイル操作の Undo／Redo 履歴。ツリーとファイル一覧ペインで 1 本を共有する
        // （部屋の中のファイル操作は、どのペインから行っても同じ履歴に積む）。
        services.AddSingleton<FileOperationHistory>();
        services.AddSingleton<FolderTreeCommandHandler>();
        services.AddSingleton<FolderTreeQuery>();
        services.AddSingleton<FilePropertiesService>();
        services.AddSingleton<IShellFileOperations, ShellFileOperations>();
        services.AddSingleton<WorkspaceListViewModel>();
        services.AddSingleton<TaskbarWorkspaceRecentService>();
        services.AddSingleton<FolderTreeViewModel>();
        // ピン留めの持ち主はツリー1つ。ファイル一覧ペインは interface 越しに同じインスタンスを見る
        // （具象＋interface の二重登録・§26.10）。
        services.AddSingleton<IFolderPinStore>(sp => sp.GetRequiredService<FolderTreeViewModel>());
        services.AddSingleton<IFilePlacesProvider, WindowsFilePlacesProvider>();
        services.AddSingleton<IQuickAccessService, WindowsQuickAccessService>();
        // ファイル一覧ペイン。操作の実体はツリーと同じ FolderTreeCommandHandler だが、
        // こちらは「ワークスペース外でも操作できる版」を渡す——外のフォルダーも開けるファイラなので、
        // エージェント用の限定（§10）を人間に被せない。
        services.AddSingleton(sp => new FilesPaneViewModel(
            sp.GetRequiredService<IWorkspaceService>(),
            FolderTreeCommandHandler.Unconfined(sp.GetRequiredService<IWorkspaceService>(),
                sp.GetRequiredService<FileOperationHistory>()),
            sp.GetRequiredService<IFolderPinStore>(),
            sp.GetRequiredService<IFilePlacesProvider>(),
            sp.GetRequiredService<FolderTreeViewModel>(),
            sp.GetRequiredService<IFileThumbnailService>(),
            sp.GetRequiredService<RecentItemsViewModel>()));
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
        // ブックマーク・履歴はワークスペースをまたぐ資産なので、アプリ単位の1ファイルに持つ。
        services.AddSingleton<BrowserLibraryStore>();
        services.AddSingleton<BrowserViewModel>();
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
            sp.GetRequiredService<IBrowserService>(),
            new sk0ya.Loomo.Core.Debug.DebugLaunchProfileStore(System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "Loomo", "tsLaunchProfiles.json"))));
        services.AddSingleton<TrailStore>();
        services.AddSingleton<TrailViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<ShellWindow>();

        return services;
    }

    /// <summary>
    /// LSP の本番配線（設計書 §30）。セッションはワークスペース単位でアプリに1つ、
    /// 拡張子→サーバーの対応表は**アプリ全体で1インスタンス**。設定画面（<c>LspManagementService</c>）・
    /// エディタの <c>:Lsp*</c>（<c>ILspServerAdmin</c>）・サーバー解決（<c>LspWorkspaceService</c>）が
    /// 別々の表を見ていたのが以前の分裂の原因なので、ここで同一インスタンスに束ねる。
    ///
    /// <para>テストからも呼べるよう切り出してある — 「テストは表を注入するので本番の配線だけが
    /// 検証対象外」という穴を塞ぐため（§30.7）。</para>
    /// </summary>
    internal static void AddLoomoLsp(IServiceCollection services, string? serverTableStorePath = null)
    {
        services.AddSingleton(_ => new sk0ya.Loomo.Services.Lsp.LspServerTable(
            serverTableStorePath ?? sk0ya.Loomo.Services.Lsp.LspServerTable.DefaultStorePath()));
        services.AddSingleton<Editor.Core.Lsp.ILspServerAdmin>(sp =>
            sp.GetRequiredService<sk0ya.Loomo.Services.Lsp.LspServerTable>());
        AddAliasedSingleton<sk0ya.Loomo.Services.Lsp.LspWorkspaceService, Editor.Core.Lsp.ILspWorkspace>(services);
        services.AddSingleton<sk0ya.Loomo.Services.Lsp.LspManagementService>();
    }

    /// <summary>
    /// 整形レジストリの本番配線。Loomo が <see cref="Editor.Core.Engine.VimEngineServices"/> を所有し、
    /// 設定画面と全エディタタブへ同じ <see cref="Editor.Core.Formatting.FormatterRegistry"/> を渡す。
    /// </summary>
    internal static void AddLoomoFormatting(IServiceCollection services, string? formatterStorePath = null)
    {
        services.AddSingleton(_ => Editor.Core.Engine.VimEngineServices.CreateApplication(
            formatterStorePath ?? System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "Loomo", "formatters.json")));
        services.AddSingleton(sp =>
            sp.GetRequiredService<Editor.Core.Engine.VimEngineServices>().Formatters);
        services.AddSingleton<sk0ya.Loomo.Services.Formatting.FormatterManagementService>();
    }

    private static void AddAliasedSingleton<TImplementation, TService>(IServiceCollection services)
        where TImplementation : class, TService
        where TService : class
    {
        services.AddSingleton<TImplementation>();
        services.AddSingleton<TService>(sp => sp.GetRequiredService<TImplementation>());
    }
}
