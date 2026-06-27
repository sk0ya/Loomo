using System;
using System.IO;
using System.Windows;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace sk0ya.Loomo.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupProfiler.Mark("OnStartup 開始");

        // エディタ構築の初回JITコスト（.vimrc 解析パス, 約250ms）をバックグラウンドで先に償却する。
        // WPF 初期化と並行して走らせ、最初の VimEditorControl 生成がウォーム済みパス（約1ms）に
        // 当たるようにする。fire-and-forget が想定された使い方（プロセス毎に一度だけ実行・スレッド安全）。
        _ = Editor.Controls.VimEditorControl.WarmUpAsync();

        base.OnStartup(e);

        // 汎用ホスト（Host.CreateDefaultBuilder）は appsettings.json 探索・環境変数/コマンドライン構成・
        // 既定ロギングプロバイダ（Console/Debug/EventSource/EventLog）の登録を起動毎に行うが、本アプリは
        // どれも使わない。臨界パスを縮めるため最小の ServiceProvider を直接構築する。
        var services = new ServiceCollection();
        ConfigureServices(services);
        _services = services.BuildServiceProvider();
        StartupProfiler.Mark("ServiceProvider 構築完了");

        // 保存済み設定（プロバイダ・APIキー等）を起動時に反映する
        var settings = _services.GetRequiredService<AiSettings>();
        _services.GetRequiredService<AiSettingsStore>().Load(settings);
        StartupProfiler.Mark("設定ロード完了");

        // 言語サーバー（LSP）の対応表＝エディタの LspServerRegistry を、Loomo 配下に永続化させる
        // （%APPDATA%/Loomo/lsp-servers.json）。エディタコントロールを生成する前に一度だけ向け直す。
        Editor.Core.Lsp.LspServerRegistry.ConfigureDefault(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Loomo", "lsp-servers.json"));

        // 整形フォーマッタ（拡張子→CLI）の対応表＝エディタの FormatterRegistry も Loomo 配下に
        // 永続化させる（%APPDATA%/Loomo/formatters.json）。:Format 実行時にユーザーが選んだ／自動
        // 検出されたフォーマッタがここに保存される。同じく一度だけ向け直す。
        Editor.Core.Formatting.FormatterRegistry.ConfigureDefault(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Loomo", "formatters.json"));

        // 保存済みカラーテーマ・アクセントカラーを適用する
        _services.GetRequiredService<ThemeManager>().Apply(settings.Theme, settings.AccentColor);
        StartupProfiler.Mark("テーマ適用完了");

        // ワークスペース開始時にローカルLLMを非同期でウォームアップする。
        _services.GetRequiredService<LocalLlmWarmupService>();
        StartupProfiler.Mark("ウォームアップ起動完了");

        var shell = _services.GetRequiredService<ShellWindow>();
        StartupProfiler.Mark("ShellWindow 解決完了");
        shell.ContentRendered += (_, _) => StartupProfiler.Mark("ContentRendered（初フレーム）");
        shell.Show();
        StartupProfiler.Mark("Show() 呼び出し完了");
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // ILogger<T> の供給のみ（プロバイダは登録しない＝出力なし・起動コストなし）。
        // AgentOrchestrator が ILogger<AgentOrchestrator> を要求するため必要。
        services.AddLogging();

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

        services.AddSingleton<WorkspaceSearchService>();
        services.AddSingleton<IWorkspaceSearchService>(sp => sp.GetRequiredService<WorkspaceSearchService>());

        services.AddSingleton<TerminalService>();
        services.AddSingleton<ITerminalService>(sp => sp.GetRequiredService<TerminalService>());

        services.AddSingleton<EditorService>();
        services.AddSingleton<IEditorService>(sp => sp.GetRequiredService<EditorService>());

        // 言語サーバー（LSP）管理：導入状況の検出・見えるターミナルでのインストール・追加削除・促し判定。
        services.AddSingleton<sk0ya.Loomo.Services.Lsp.LspManagementService>();

        // デバッグ実行（DAP / netcoredbg）。Phase 1：起動して実行・出力を観測する。
        services.AddSingleton<sk0ya.Loomo.Core.Debug.IDebugService,
            sk0ya.Loomo.Services.Debug.NetcoredbgDebugService>();

        services.AddSingleton<BrowserService>();
        services.AddSingleton<IBrowserService>(sp => sp.GetRequiredService<BrowserService>());

        services.AddSingleton<UiApprovalService>();
        services.AddSingleton<IApprovalService>(sp => sp.GetRequiredService<UiApprovalService>());

        // Git クライアント（git CLI を独立プロセスで実行。サイドバー・Git ペイン双方が共有）
        services.AddSingleton<GitService>();

        // --- AI ---
        // ローカル推論エンジン（CPU）。モデルを常駐させるためシングルトン。バックエンドは modelPath で振り分ける：
        //   .gguf ファイル → llama.cpp（LlamaCppEngine）／ genai_config.json を持つフォルダ → ONNX（OnnxGenAiEngine）。
        services.AddSingleton<OnnxGenAiEngine>();
        services.AddSingleton<LlamaCppEngine>();
        // ルータは concrete でも登録（ウォームアップが modelPath で暖機対象エンジンを選ぶのに使う）。
        services.AddSingleton<LocalInferenceRouter>();
        services.AddSingleton<ILocalInferenceEngine>(sp => sp.GetRequiredService<LocalInferenceRouter>());
        services.AddSingleton<IAiClientFactory, AiClientFactory>();
        // コンテキスト長管理：現在プロバイダの上限に合わせ送信前に履歴をトリム
        services.AddSingleton<IContextWindowPolicy, SettingsContextWindowPolicy>();
        // ローカルに配置済みの ONNX モデルフォルダを列挙（設定画面の選択肢）
        services.AddSingleton(sp => new ModelCatalogService(sp.GetRequiredService<AiSettings>()));
        // モデルのダウンロード（Hugging Face → %APPDATA%/Loomo/models）
        services.AddSingleton(sp => new ModelDownloadService(
            sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient("ai")));

        // --- ツール ---
        // 基本は run_powershell（PowerShell 実行）で読み取り・検索・ビルド・テストを行う。
        // ファイルの新規作成／編集だけは構造化ツール（write_file/edit_file）に分離する：
        //   - 内容を独立 JSON 引数で渡すので「PS 構文＋JSON の二重エスケープ」を避けられ、小モデルの失敗が減る
        //   - ResolvePath によるワークスペースルート制限と承認カードの差分表示（安全性）が戻る
        // ONNX 化で system＋tools の安定プレフィックスが KV キャッシュ再利用されるため（OnnxGenAiEngine）、
        // ツール定義の prefill は初回/暖機 1 回に償却され、数個の追加は毎ターンの負担にならない。
        services.AddSingleton<IAgentTool, PwshTool>();
        services.AddSingleton<IAgentTool, WriteFileTool>();
        services.AddSingleton<IAgentTool, EditFileTool>();
        // ワークスペース外の情報を調べるブラウザ検索。可視ブラウザペインのアクティブタブ（IBrowserService）で
        // 検索し、結果ページのテキストを返す（別ウィンドウは起動しない）。
        services.AddSingleton<IAgentTool, WebSearchTool>();
        services.AddSingleton<ToolRegistry>();

        // 「ツールの実装が内部で AI を活用する」ための基盤（注入用）。エージェントに委譲ツールを提示するのではなく、
        // 将来 AI を内部利用するツールが ISubAgentRunner を受け取り、隔離された AI サブタスク（要約・分類・整形・調査）を
        // 回して結果だけ受け取る。現状は消費者なし＝解決もされない（遅延）。実体 SubAgentRunner は IServiceProvider 経由で
        // AgentOrchestrator を遅延解決し、ツール→実行器→オーケストレータ→ToolRegistry→ツールの DI 循環を回避する。
        services.AddSingleton<ISubAgentRunner, SubAgentRunner>();

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
        // AI のファイル変更（write_file/edit_file）を前後全文付きで記録するジャーナル（Diff セッションが読む）
        services.AddSingleton<FileChangeJournal>();
        services.AddSingleton<IFileChangeJournal>(sp => sp.GetRequiredService<FileChangeJournal>());
        services.AddSingleton<AgentOrchestrator>();
        services.AddSingleton<ConversationStore>();
        services.AddSingleton<WorkflowStore>();

        // --- トレース読取（AIセッション一覧の所要時間表示などに利用）---
        services.AddSingleton<TraceReader>();

        // --- EditorSupport（アクティブなエディタのファイルに応じた自動表示。拡張子対応はここへ追加登録） ---
        services.AddSingleton<IEditorSupportProvider, MarkdownEditorSupport>();
        services.AddSingleton<IEditorSupportProvider, JsonEditorSupport>();     // JSON を折りたたみツリーで表示
        services.AddSingleton<IEditorSupportProvider, ImageEditorSupport>();
        services.AddSingleton<IEditorSupportProvider, VGridEditorSupport>();   // CSV/TSV を VGrid グリッドで表示
        services.AddSingleton<IEditorSupportProvider, BrowserEditorSupport>(); // PDF/SVG/HTML 等はブラウザ(WebView2)で表示
        services.AddSingleton<EditorSupportRegistry>();

        // --- ViewModels / Window ---
        services.AddSingleton<ThemeManager>();
        services.AddSingleton<LocalLlmWarmupService>();
        services.AddSingleton<IAiWarmup>(sp => sp.GetRequiredService<LocalLlmWarmupService>());
        services.AddSingleton<WorkspaceStateStore>();
        services.AddSingleton<PromptHistoryStore>();
        services.AddSingleton<TabIconService>();
        services.AddSingleton<Input.KeybindingService>();
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
        services.AddSingleton<KeybindingsViewModel>();
        services.AddSingleton<GitPanelViewModel>();
        services.AddSingleton<GitSessionViewModel>();
        services.AddSingleton<DiffSessionViewModel>();
        services.AddSingleton<TraceSessionViewModel>();
        services.AddSingleton<PegboardViewModel>();
        services.AddSingleton<SearchPanelViewModel>();
        services.AddSingleton<DebugViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<ShellWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
