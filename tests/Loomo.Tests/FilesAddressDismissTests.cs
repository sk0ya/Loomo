using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.Core.Agent;

namespace sk0ya.Loomo.Tests;

/// <summary>住所欄（ファイル一覧のアドレスバー）の畳み方を実ビューで見る。
///
/// <para>「フォーカスが外れたら畳む」だけでは畳めない道が残っていた——WPF はフォーカスを
/// 取れない要素（ツールバーの余白・見出し・他ペインの地の部分）を押してもキーボード
/// フォーカスを動かさないので <c>LostKeyboardFocus</c> が鳴らず、住所欄が開きっぱなしになる。
/// ここは入力欄の内と外の押下で、開閉が期待どおりに動くことを固定する。</para></summary>
[Collection(WpfViewTests.Name)]
public sealed class FilesAddressDismissTests
{
    // ビューは共有の STA ホスト上で組み立てる（WpfViewTests のコレクション）。
    private readonly WpfViewHost _host;

    public FilesAddressDismissTests(WpfViewHost host) => _host = host;

    [Fact]
    public void 住所欄の外を押したら畳み内側を押しても畳まない()
    {
        RunSta(() =>
        {
            using var pane = OpenPane();
            var (view, window, column) = (pane.View, pane.Window, pane.Column);

            column.BeginAddressEdit();
            window.UpdateLayout();
            Assert.True(column.IsAddressEditing);
            var box = (UIElement)view.FindName("AddressBox")!;
            box.Focus();
            window.UpdateLayout();
            Assert.True(box.IsKeyboardFocusWithin, "入力欄がキーボードフォーカスを取れていない");

            // 入力欄の内側の押下では畳まない（打ち込む前に消えてしまう）。
            var editor = (UIElement)view.FindName("AddressEditor")!;
            PressLeftButton(editor);
            window.UpdateLayout();
            Assert.True(column.IsAddressEditing, "入力欄を押しただけで畳んでしまった");

            // 外側の押下で畳む。フォーカスが動かない要素でも畳めることが要点なので、
            // フォーカスは触らずに押下だけを流す。
            PressLeftButton(view);
            window.UpdateLayout();
            Assert.False(column.IsAddressEditing, "住所欄の外を押しても畳めていない");

            // 畳んだ入力欄は Collapsed になる。フォーカスがそこに残ったままだと WPF は
            // ウィンドウの根へ落とし、Ctrl+L も一覧のカーソル移動も効かなくなるので、
            // 誰もフォーカスを取らなかったときは一覧へ返す。
            Pump();
            Assert.True(view.IsKeyboardFocusWithin,
                "畳んだあとフォーカスがウィンドウの根へ落ちたままになっている");
        });
    }

    /// <summary>畳んだあとのフォーカス戻しは押下の処理が終わってからなので、一度回す。
    ///
    /// <para>フォーカス戻しと<b>同じ Input 優先度</b>で回すこと（同一優先度は先入れ先出しなので、
    /// この目印が動いた時点で戻しは済んでいる）。Background まで落として回すと、他のテストが
    /// 作ったビューモデルの遅延リフレッシュ（<c>DebouncedFolderWatcher</c> は
    /// <c>Application.Current.Dispatcher</c> へ Background で積む）まで、このスレッドで
    /// 実行してしまい、別スレッドの CollectionView を触ってテストホストごと落ちる。</para></summary>
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Input, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>住所欄はパンくずと入れ替わるだけで、行の高さを動かしてはいけない
    /// （開くたびに下の一覧ごと上下する）。UI の文字サイズはユーザー設定で変わり、
    /// 既定は等倍(13)ではなく 16px なので、<b>実際に使われる倍率</b>でも見る。</summary>
    [Theory]
    [InlineData(UiFontManager.ReferenceSize)]   // 等倍
    [InlineData(UiFontManager.DefaultSize)]     // 既定
    [InlineData(22)]                            // 大きめ
    public void 住所欄を開いてもナビゲーション行の高さは変わらない(double baseFontSize)
    {
        RunSta(() =>
        {
            var fonts = new UiFontManager();
            fonts.Apply(baseFontSize);
            try
            {
                using var pane = OpenPane();
                var nav = (FrameworkElement)pane.View.FindName("NavRow")!;
                var browsing = nav.ActualHeight;
                Assert.True(browsing > 0);

                pane.Column.BeginAddressEdit();
                pane.Window.UpdateLayout();

                Assert.Equal(browsing, nav.ActualHeight);
            }
            finally { fonts.Apply(UiFontManager.ReferenceSize); }
        });
    }

    /// <summary>一時フォルダーを開いたファイル一覧ペインを1つ、実ウィンドウに載せて返す。</summary>
    private static Pane OpenPane()
    {
        var root = Path.Combine(Path.GetTempPath(), $"loomo-address-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "child"));
        File.WriteAllText(Path.Combine(root, "note.txt"), "x");

        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(root);
        var tree = new FolderTreeViewModel(workspace, new FakeAiWarmup(),
            new WorkflowStore(Path.Combine(Path.GetTempPath(), $"loomo-address-wf-{Guid.NewGuid():N}")),
            new FolderTreeCommandHandler(workspace, new FileOperationHistory()), new FolderTreeQuery());
        var column = new FilesColumnViewModel(
            workspace, FolderTreeCommandHandler.Unconfined(workspace, new FileOperationHistory()),
            tree, new FakeFilePlacesProvider());
        column.Restore(snapshot: null, fallbackFolder: root);

        var view = new FilesColumnView { DataContext = column };
        var window = new Window
        {
            Width = 640,
            Height = 420,
            Content = view,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
        };
        window.Show();
        window.UpdateLayout();
        return new Pane(view, window, column, root);
    }

    private sealed record Pane(
        FilesColumnView View, Window Window, FilesColumnViewModel Column, string Root) : IDisposable
    {
        public void Dispose()
        {
            Window.Close();
            Column.Dispose();
            try { Directory.Delete(Root, recursive: true); } catch { /* 一時フォルダの削除失敗は無視 */ }
        }
    }

    private static void PressLeftButton(UIElement target)
        => target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseDownEvent,
            Source = target,
        });

    private void RunSta(Action body) => _host.Run(body);
}
