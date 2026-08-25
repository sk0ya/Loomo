using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace sk0ya.Loomo.Tests;

/// <summary>ファイル一覧ペインのビューを実際に組み立てるテストのコレクション。
///
/// <para>この種のテストは STA スレッドと <see cref="Application"/>（テーマ辞書・暗黙スタイルの
/// 置き場）を必要とするが、<see cref="Application.Current"/> はプロセスで1つしか持てず、その
/// リソース辞書の中身（ブラシ等の Freezable）は<b>作ったスレッドのもの</b>になる。テストごとに
/// STA スレッドを立てて捨てる書き方だと、2つ目以降のビューは「もう終了したスレッドが持つ
/// リソース」を引くことになり、Null や
/// 「non-concurrent collections must have exclusive access」で落ちる。
///
/// <para>そこで STA スレッドと <see cref="Application"/> をコレクション共有のフィクスチャに1つだけ持ち、
/// このコレクションのテストは全部その1本の上で（＝直列に）走らせる。</para></summary>
[CollectionDefinition(Name)]
public sealed class WpfViewTests : ICollectionFixture<WpfViewHost>
{
    public const string Name = "wpf-view";
}

/// <summary>ビューを作るテストが相乗りする、生きたままの STA ディスパッチャ。</summary>
public sealed class WpfViewHost : IDisposable
{
    private readonly Dispatcher _dispatcher;

    public WpfViewHost()
    {
        using var ready = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            if (Application.Current is null)
            {
                // アプリ本体の App は使わない——ここでメッセージループを回すと OnStartup が走り、
                // DI コンテナと ShellWindow まで組み上がってしまう。必要なのはテーマ辞書だけ。
                // 最後のウィンドウを閉じても止まらないよう、終了は明示のみにする。
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (var name in new[] { "Palette.Dark", "Typography", "Controls" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri(
                            $"pack://application:,,,/sk0ya.Loomo.App;component/Themes/{name}.xaml",
                            UriKind.Absolute),
                    });
            }
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "wpf-view-host",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        _dispatcher = dispatcher!;
    }

    /// <summary>ビュー操作を STA スレッドで実行し、例外は呼び出し元へそのまま投げ直す。</summary>
    public void Run(Action body)
    {
        ExceptionDispatchInfo? error = null;
        _dispatcher.Invoke(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        });
        error?.Throw();
    }

    public void Dispose() => _dispatcher.InvokeShutdown();
}
