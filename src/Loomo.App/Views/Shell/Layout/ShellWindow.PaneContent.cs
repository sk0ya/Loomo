using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: ペイン本体（中身のView）の遅延実体化。
/// <para>ペインの枠と見出しは ShellWindow.xaml が持つが、<b>中身</b>は初めて見えたときに作る。
/// 12枚のペインのうち起動直後に配置へ載るのはたいてい2〜3枚で、残りの View まで
/// <c>InitializeComponent</c> で組み立てると、その場では誰も見ないものに時間を払うことになる
/// （実測で View 群の構築だけで数百ms）。エディタタブの遅延実体化やブラウザタブの
/// <c>PendingUrl</c> と同じ考え方。</para>
/// <para>実体化の合図は <see cref="UIElement.IsVisible"/>——ペインを配置へ載せる側
/// （<c>BuildNode</c>／ズーム／ステージ／ドック）は必ず <see cref="Visibility.Visible"/> を立てるので、
/// 経路ごとに呼び出しを足さなくても拾える。袖のミニチュア用に実体ペインを描画元へ移す経路も同じ。</para>
/// </summary>
public partial class ShellWindow
{
    private readonly Dictionary<ContentControl, Func<FrameworkElement>> _deferredPaneContent = new();

    /// <summary>いま <see cref="RealizePaneContent"/> の最中のホスト（再入を見分けるためだけの印）。</summary>
    private readonly HashSet<ContentControl> _realizingPaneContent = new();

    /// <summary>ペインの中身の作り方を登録する（この時点では作らない）。</summary>
    private void DeferPaneContent(ContentControl host, Func<FrameworkElement> factory)
    {
        _deferredPaneContent[host] = factory;
        host.IsVisibleChanged += OnDeferredPaneContentVisibleChanged;
    }

    private void OnDeferredPaneContentVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is ContentControl host)
            RealizePaneContent(host);
    }

    /// <summary>登録済みの中身を（まだなら）作って返す。見える前に触りたい側の入口。</summary>
    private T PaneContent<T>(ContentControl host) where T : FrameworkElement
        => (T)RealizePaneContent(host);

    /// <summary>登録済みの中身を作って据える（作ってあればそれを返す）。
    /// <para><b>作り損ねたホストを壊さない</b>——登録を外すのも購読を切るのも
    /// <c>host.Content</c> に据わってからで、View の ctor が投げたら登録は残したまま抜ける。
    /// 先に外していたときは、一度投げたホストが以後ずっと「登録していないペインホストです」を
    /// 投げ続ける置物になり、しかもそれが <see cref="UIElement.IsVisibleChanged"/> の中＝配置の
    /// 組み立て中に飛ぶので、ペイン1枚が空になる代わりにウィンドウごと落ちていた。
    /// ここを通さずに投げれば、落ちる場所も理由も本物の例外のまま残る（遅延にする前＝
    /// <c>InitializeComponent</c> で組んでいた頃と同じ）。</para></summary>
    private FrameworkElement RealizePaneContent(ContentControl host)
    {
        if (host.Content is FrameworkElement realized)
            return realized;

        if (!_deferredPaneContent.TryGetValue(host, out var factory))
            throw new InvalidOperationException("遅延実体化を登録していないペインホストです。");

        // View の ctor が（テーマ適用などで）巡り巡ってここへ戻ってきても二重には作らない。
        // まだ何も無い以上その場で返せる中身は無いので、黙って別物を作らず、素直に投げる。
        if (!_realizingPaneContent.Add(host))
            throw new InvalidOperationException("ペイン本体の実体化が自分自身を呼び戻しています。");

        FrameworkElement view;
        try
        {
            view = factory();
            host.Content = view;
        }
        finally
        {
            _realizingPaneContent.Remove(host);
        }

        // 一度据われば合図は要らない（以降の表示切替で毎回ここへ戻ってこない）。
        _deferredPaneContent.Remove(host);
        host.IsVisibleChanged -= OnDeferredPaneContentVisibleChanged;
        return view;
    }
}
