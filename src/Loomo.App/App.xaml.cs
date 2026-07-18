using System;
using System.Windows;
using sk0ya.Loomo.App.DependencyInjection;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.Core.Observability;
using Microsoft.Extensions.DependencyInjection;

namespace sk0ya.Loomo.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupProfiler.Mark("OnStartup 開始");

        // 未処理例外でアプリが落ちるとき、Debug.Assert で例外の詳細（型・メッセージ・スタック）を出す。
        // Debug ビルドではアサートダイアログに全文が表示され、その場で内訳を確認できる（Release は無効）。
        RegisterUnhandledExceptionAsserts();

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

        _services.GetRequiredService<AppBootstrapper>().Initialize();

        var shell = _services.GetRequiredService<ShellWindow>();
        StartupProfiler.Mark("ShellWindow 解決完了");
        shell.ContentRendered += (_, _) => StartupProfiler.Mark("ContentRendered（初フレーム）");
        shell.Show();
        StartupProfiler.Mark("Show() 呼び出し完了");
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services
            .AddLoomoCore()
            .AddLoomoAi()
            .AddLoomoGit()
            .AddLoomoEditorSupport()
            .AddLoomoPresentation();
    }

    // アプリを落としうる 3 系統の未処理例外をそれぞれ捕まえ、Debug.Assert で詳細を出す。
    //   - DispatcherUnhandledException : UI スレッド（大半のクラッシュ）
    //   - AppDomain.UnhandledException : UI 以外のスレッド（バックグラウンドスレッド等）
    //   - TaskScheduler.UnobservedTaskException : await されず GC された Task の例外
    private void RegisterUnhandledExceptionAsserts()
    {
        DispatcherUnhandledException += (_, args) =>
            AssertException(args.Exception, "Dispatcher（UI スレッド）");

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AssertException(args.ExceptionObject as Exception, "AppDomain（非 UI スレッド）");

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
            AssertException(args.Exception, "未観測 Task");
    }

    private static void AssertException(Exception? ex, string source)
    {
        if (ex is null)
            return;

        // ToString() は型・メッセージ・スタックトレース・InnerException 連鎖まで含む。
        System.Diagnostics.Debug.Assert(false, $"未処理例外（{source}）", ex.ToString());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
