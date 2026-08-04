using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

using sk0ya.Loomo.App.Input;
using sk0ya.Loomo.App.Services;

using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 設定ウィンドウを挟んだあとの「フォーカスをどこへ戻すか」の固定（設計書 §31.8 Phase 5 の完了条件
/// 「舞台切替、デタッチ復帰、設定オーバーレイを挟んでもキャレット／選択／入力先が予測可能である」）。
///
/// <para>不具合の実測（2026-08-02、docs/検証/IDE体感品質チェックリスト.md の 20:57–21:03 追試）は、
/// Escape で設定を閉じた直後のフォーカスが直前の入力先ではなくブラウザペインの WebView Document を
/// 指す、というもの。戻り先は「閉じた時点でどこにフォーカスがあるか」ではなく「開く直前に覚えた
/// 最後の内部フォーカス」だけで決まる、という点をここで固定する。</para>
/// </summary>
public class FocusReturnPolicyTests
{
    private static readonly Guid Viewport = Guid.NewGuid();

    // ===== 戻り先の決定（純ロジック） =====

    [Fact]
    public void 覚えていた要素が生きていればその要素へ戻す()
    {
        var decision = FocusReturnPolicy.Decide(
            FocusReturnOrigin.Viewport(PaneKind.Editor, Viewport),
            elementAlive: true, paneAvailable: true, viewportAlive: true, sidebarVisible: true);

        Assert.Equal(FocusReturnKind.Element, decision.Kind);
        Assert.Equal(PaneKind.Editor, decision.Pane);
        Assert.Equal(Viewport, decision.ViewportId);
    }

    [Fact]
    public void 要素が消えてもビューポートが残っていればそのビューポートへ戻す()
    {
        var decision = FocusReturnPolicy.Decide(
            FocusReturnOrigin.Viewport(PaneKind.Editor, Viewport),
            elementAlive: false, paneAvailable: true, viewportAlive: true, sidebarVisible: false);

        Assert.Equal(FocusReturnKind.Viewport, decision.Kind);
        Assert.Equal(PaneKind.Editor, decision.Pane);
        Assert.Equal(Viewport, decision.ViewportId);
    }

    [Fact]
    public void ビューポートも消えていればペインへ戻す()
    {
        var decision = FocusReturnPolicy.Decide(
            FocusReturnOrigin.Viewport(PaneKind.Terminal, Viewport),
            elementAlive: false, paneAvailable: true, viewportAlive: false, sidebarVisible: false);

        Assert.Equal(FocusReturnKind.Pane, decision.Kind);
        Assert.Equal(PaneKind.Terminal, decision.Pane);
    }

    [Fact]
    public void 分割していないペインは要素が消えたらペインへ戻す()
    {
        var decision = FocusReturnPolicy.Decide(
            FocusReturnOrigin.Of(PaneKind.Git),
            elementAlive: false, paneAvailable: true, viewportAlive: false, sidebarVisible: false);

        Assert.Equal(FocusReturnKind.Pane, decision.Kind);
        Assert.Equal(PaneKind.Git, decision.Pane);
    }

    [Fact]
    public void ペインが非表示なら要素が生きていても戻さない()
    {
        var decision = FocusReturnPolicy.Decide(
            FocusReturnOrigin.Viewport(PaneKind.Editor, Viewport),
            elementAlive: true, paneAvailable: false, viewportAlive: true, sidebarVisible: true);

        Assert.Equal(FocusReturnKind.None, decision.Kind);
    }

    [Fact]
    public void サイドバー起点は要素が生きていればその要素へ戻す()
    {
        var decision = FocusReturnPolicy.Decide(
            FocusReturnOrigin.Sidebar,
            elementAlive: true, paneAvailable: false, viewportAlive: false, sidebarVisible: true);

        Assert.Equal(FocusReturnKind.Element, decision.Kind);
        Assert.Null(decision.Pane);
    }

    [Fact]
    public void サイドバー起点で要素が消えていればサイドバーへ戻す()
    {
        var decision = FocusReturnPolicy.Decide(
            FocusReturnOrigin.Sidebar,
            elementAlive: false, paneAvailable: false, viewportAlive: false, sidebarVisible: true);

        Assert.Equal(FocusReturnKind.Sidebar, decision.Kind);
    }

    [Fact]
    public void サイドバーが閉じていれば戻さない()
    {
        var decision = FocusReturnPolicy.Decide(
            FocusReturnOrigin.Sidebar,
            elementAlive: false, paneAvailable: false, viewportAlive: false, sidebarVisible: false);

        Assert.Equal(FocusReturnKind.None, decision.Kind);
    }

    [Fact]
    public void 起点を覚えていなければ戻さない()
    {
        var decision = FocusReturnPolicy.Decide(
            origin: null,
            elementAlive: true, paneAvailable: true, viewportAlive: true, sidebarVisible: true);

        Assert.Equal(FocusReturnKind.None, decision.Kind);
    }

    // ===== 要素の生存判定と実際の復帰（FocusReturnElement）。UI 型を扱うため STA。 =====

    [Fact]
    public void 別要素がフォーカスを奪っても覚えていた要素へ戻せる()
    {
        // 実測不具合の再現形：設定を閉じた直後にブラウザ（ここでは別の入力要素）がフォーカスを持って
        // いても、覚えていた起点だけから戻り先を決めれば元の入力先へ戻る。
        RunSta(() =>
        {
            var editor = new TextBox();
            var browser = new TextBox();
            var root = new Grid();
            root.Children.Add(editor);
            root.Children.Add(browser);
            var window = new Window { Width = 320, Height = 180, Content = root, ShowInTaskbar = false };
            try
            {
                window.Show();
                Pump();
                var remembered = Reference(editor);
                editor.Focus();
                Pump();

                browser.Focus();        // 設定ウィンドウを閉じた直後の横取りに相当
                Pump();
                Assert.Same(browser, FocusManager.GetFocusedElement(window));

                var target = FocusReturnElement.ResolveLive(remembered, window);
                var decision = FocusReturnPolicy.Decide(
                    FocusReturnOrigin.Of(PaneKind.Editor),
                    elementAlive: target is not null, paneAvailable: true, viewportAlive: false, sidebarVisible: false);

                Assert.Equal(FocusReturnKind.Element, decision.Kind);
                target!.Focus();
                Pump();
                Assert.Same(editor, FocusManager.GetFocusedElement(window));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void 折り畳まれた要素は戻り先にしない()
    {
        RunSta(() =>
        {
            var inner = new TextBox();
            var window = new Window { Width = 320, Height = 180, Content = inner, ShowInTaskbar = false };
            try
            {
                window.Show();
                Pump();
                Assert.NotNull(FocusReturnElement.ResolveLive(Reference(inner), window));

                inner.Visibility = Visibility.Collapsed;    // タブを閉じた／ペインを隠した相当
                Pump();

                Assert.Null(FocusReturnElement.ResolveLive(Reference(inner), window));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void 本体から外れた要素は戻り先にしない()
    {
        RunSta(() =>
        {
            var inner = new TextBox();
            var window = new Window { Width = 320, Height = 180, Content = inner, ShowInTaskbar = false };
            var other = new Window { Width = 320, Height = 180, ShowInTaskbar = false };
            try
            {
                window.Show();
                other.Show();
                Pump();

                // 切り離しウィンドウへ移った相当：本体の配下ではないので戻り先にしない。
                Assert.Null(FocusReturnElement.ResolveLive(Reference(inner), other));
            }
            finally { window.Close(); other.Close(); }
        });
    }

    [Fact]
    public void 回収済みの弱参照は戻り先にしない()
    {
        RunSta(() =>
        {
            var window = new Window { Width = 320, Height = 180, ShowInTaskbar = false };
            try
            {
                var reference = new WeakReference<IInputElement>(new TextBox());
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                Assert.Null(FocusReturnElement.ResolveLive(reference, window));
            }
            finally { window.Close(); }
        });
    }

    private static WeakReference<IInputElement> Reference(IInputElement element) => new(element);

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { exception = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (exception is not null) throw exception;
    }
}
