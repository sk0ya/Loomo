using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using sk0ya.Loomo.App.Detach;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>切り離しウィンドウの項目ホスティング（アクティブ切替・可視制御・破棄）の固定。UI 型を扱うため STA。</summary>
public class DetachedPaneWindowTests
{
    [Fact]
    public void 最大化解除ドラッグでは掴んだ横位置の比率を維持する()
    {
        var position = DetachedPaneWindow.CalculateRestoredTopLeft(
            cursor: new Point(1200, 20),
            captionPoint: new Point(960, 12),
            maximizedWidth: 1920,
            restoredBounds: new Rect(100, 100, 1000, 700));

        Assert.Equal(new Point(700, 8), position);
    }

    [Fact]
    public void 最大化解除ドラッグの横位置比率はウィンドウ内に制限する()
    {
        var position = DetachedPaneWindow.CalculateRestoredTopLeft(
            cursor: new Point(1200, 20),
            captionPoint: new Point(2500, 12),
            maximizedWidth: 1920,
            restoredBounds: new Rect(100, 100, 1000, 700));

        Assert.Equal(new Point(200, 8), position);
    }

    // タブ3枚：A[0..160] B[160..310] C[310..430]（中心 80 / 235 / 370）。
    private static readonly double[] Centers = { 80.0, 235.0, 370.0 };

    [Fact]
    public void 右へ運んだタブは右端が隣の中心を越えたところで入れ替わる()
    {
        // A(幅160)を掴んで右へ。右端が B の中心(235)を越える＝左端 75 を過ぎたら B と入れ替わる。
        Assert.Equal(0, DetachedPaneWindow.CalculateReorderIndex(Centers, 0, draggedLeft: 70, draggedRight: 230));
        Assert.Equal(1, DetachedPaneWindow.CalculateReorderIndex(Centers, 0, draggedLeft: 80, draggedRight: 240));
        Assert.Equal(2, DetachedPaneWindow.CalculateReorderIndex(Centers, 0, draggedLeft: 220, draggedRight: 380));
    }

    [Fact]
    public void 左へ運んだタブは左端が手前の中心を越えたところで入れ替わる()
    {
        Assert.Equal(2, DetachedPaneWindow.CalculateReorderIndex(Centers, 2, draggedLeft: 240, draggedRight: 360));
        Assert.Equal(1, DetachedPaneWindow.CalculateReorderIndex(Centers, 2, draggedLeft: 230, draggedRight: 350));
        Assert.Equal(0, DetachedPaneWindow.CalculateReorderIndex(Centers, 2, draggedLeft: 70, draggedRight: 190));
    }

    [Fact]
    public void 幅の広いタブでも末尾へ届く()
    {
        // 中心どうしで比べていたときの取りこぼし：掴んだタブ(幅160)は帯の内側へクランプされるので、
        // 隣(幅150)より広いと中心が隣の中心を越えられず、右端まで運んでも末尾へ行けなかった。
        var centers = new[] { 80.0, 235.0 };   // A[0..160] B[160..310]、クランプ後の A の左端は最大 150

        Assert.Equal(1, DetachedPaneWindow.CalculateReorderIndex(centers, 0, draggedLeft: 150, draggedRight: 310));
    }

    [Fact]
    public void 入れ替えの境目は左右どちらへ運んでも同じで行き来しても暴れない()
    {
        // A を右へ運んで B と入れ替えた直後（A は index 1・左端 76）、そのまま押し戻さない限り戻らない。
        var afterSwap = new[] { 75.0, 230.0 };   // B[0..150] A[150..310]

        Assert.Equal(1, DetachedPaneWindow.CalculateReorderIndex(afterSwap, 1, draggedLeft: 76, draggedRight: 236));
        Assert.Equal(0, DetachedPaneWindow.CalculateReorderIndex(afterSwap, 1, draggedLeft: 74, draggedRight: 234));
    }

    [Fact]
    public void コンテナ未生成のタブは並べ替えの判定から外す()
    {
        // 中心が取れないタブ（NaN）は境目に数えない＝掴んだタブが勝手に飛ばない。
        var centers = new[] { 80.0, double.NaN, 370.0 };

        Assert.Equal(0, DetachedPaneWindow.CalculateReorderIndex(centers, 0, draggedLeft: 70, draggedRight: 230));
        Assert.Equal(-1, DetachedPaneWindow.CalculateReorderIndex(centers, 5, draggedLeft: 70, draggedRight: 230));
    }

    [Fact]
    public void ドラッグ中の案内は離したときに起きることを出す()
    {
        Assert.Equal("離すと新しいウィンドウ", TabDragGhost.HintFor(DragDropEffects.None));
        Assert.Equal("このタブ帯へ入れる", TabDragGhost.HintFor(DragDropEffects.Move));

        // 1枚だけの切り離し窓から引き出しても分かれない（EndDrag が弾く）ので、起きないことを約束しない。
        Assert.Equal("すでに単独のウィンドウ", TabDragGhost.HintFor(DragDropEffects.None, canSplit: false));
        Assert.Equal("このタブ帯へ入れる", TabDragGhost.HintFor(DragDropEffects.Move, canSplit: false));
    }

    [Fact]
    public void ドラッグ中の案内は窓の上なら帯の外でもタブになると出す()
    {
        // タブ帯（34px）を外しても切り離しウィンドウの上なら、離せばその窓のタブになる。
        Assert.Equal("このウィンドウのタブにする",
            TabDragGhost.HintFor(DragDropEffects.None, canSplit: true, overMergeTarget: true));
        // 帯の上（受け手が Move を返している）ならそちらの案内を優先する。
        Assert.Equal("このタブ帯へ入れる",
            TabDragGhost.HintFor(DragDropEffects.Move, canSplit: true, overMergeTarget: true));
        // 1枚だけの窓から引き出しても、相手の窓の上なら結合はできる。
        Assert.Equal("このウィンドウのタブにする",
            TabDragGhost.HintFor(DragDropEffects.None, canSplit: false, overMergeTarget: true));
    }

    [Fact]
    public void 切り離しウィンドウが出ていれば次の項目はそのタブになる()
    {
        RunSta(() =>
        {
            var owner = new Window { Width = 200, Height = 200, ShowInTaskbar = false };
            owner.Show();                       // Owner に指定するには表示済みである必要がある
            var manager = new DetachedWindowManager(owner);
            try
            {
                var first = NewItem("A");
                // 窓が1つも無ければ呼び出し側が新しい窓を開く（＝ここでは false）。
                Assert.False(manager.TryAddToRecentWindow(first));

                manager.Detach(first);
                var second = NewItem("B");
                Assert.True(manager.TryAddToRecentWindow(second));

                // 窓は増えず、2枚目はタブとして足されてアクティブになる。
                Assert.Equal(new[] { first, second }, manager.AllItems);
                Assert.True(second.IsActive);
                Assert.False(first.IsActive);
            }
            finally
            {
                manager.CloseAll();
                owner.Close();
            }
        });
    }

    [Fact]
    public void 戻せるタブはメインの帯へ返せる()
    {
        RunSta(() =>
        {
            var manager = new DetachedWindowManager(new Window());
            var window = new DetachedPaneWindow(manager);
            var returned = 0;
            var keep = NewItem("keep");                       // 窓が空になって閉じないよう1枚残す
            var item = new DetachedItem(
                DetachKind.EditorMove, "A", new Border(), icon: null, dispose: () => Assert.Fail("戻すときは破棄しない"))
            {
                Return = new DetachReturn(TabEntryKind.Editor, () => returned++),
            };
            window.AddItem(keep);
            window.AddItem(item);

            manager.BeginDrag(item, window);
            Assert.Equal(TabEntryKind.Editor, manager.DraggingReturn?.Kind);

            manager.ReturnDraggedToMain();

            Assert.Equal(1, returned);
            Assert.False(window.Contains(item));   // 窓からは外れ、実体はメイン側で生き続ける
            Assert.True(window.Contains(keep));
            manager.ClearDrag();
        });
    }

    [Fact]
    public void Escでやめたドラッグはタブを動かさない()
    {
        RunSta(() =>
        {
            var manager = new DetachedWindowManager(new Window());
            var window = new DetachedPaneWindow(manager);
            var keep = NewItem("keep");
            var item = NewItem("A");
            window.AddItem(keep);
            window.AddItem(item);

            manager.BeginDrag(item, window);
            manager.CancelDrag();
            manager.EndDrag(DragDropEffects.None);   // 離した場所で結合も分離もしない

            Assert.True(window.Contains(item));
            Assert.Equal(2, window.ItemCount);
            manager.ClearDrag();
        });
    }

    [Fact]
    public void 戻し先を持たないタブは帯に受けさせない()
    {
        RunSta(() =>
        {
            var manager = new DetachedWindowManager(new Window());
            var window = new DetachedPaneWindow(manager);
            var keep = NewItem("keep");
            var item = NewItem("Diff");            // Return なし＝Diff やプレビューの複製
            window.AddItem(keep);
            window.AddItem(item);

            manager.BeginDrag(item, window);

            Assert.Null(manager.DraggingReturn);   // メインの帯は受け口を出さない
            manager.ReturnDraggedToMain();
            Assert.True(window.Contains(item));    // 呼ばれても何も起きない
            manager.ClearDrag();
        });
    }

    [Fact]
    public void 追加した項目はアクティブになり他は退避する()
    {
        RunSta(() =>
        {
            var window = NewWindow();
            var a = NewItem("A");
            var b = NewItem("B");

            window.AddItem(a);
            window.AddItem(b);

            Assert.Equal(2, window.ItemCount);
            Assert.False(a.IsActive);
            Assert.True(b.IsActive);
            Assert.Equal(Visibility.Collapsed, a.Content.Visibility);
            Assert.Equal(Visibility.Visible, b.Content.Visibility);

            window.SetActive(a);
            Assert.True(a.IsActive);
            Assert.False(b.IsActive);
            Assert.Equal(Visibility.Visible, a.Content.Visibility);
            Assert.Equal(Visibility.Collapsed, b.Content.Visibility);
        });
    }

    [Fact]
    public void 非アクティブ項目を破棄付きで外すと破棄されアクティブは保たれる()
    {
        RunSta(() =>
        {
            var window = NewWindow();
            var disposed = 0;
            var a = NewItem("A");
            var b = NewItem("B", dispose: () => disposed++);

            window.AddItem(a);
            window.AddItem(b);
            window.SetActive(a);       // A をアクティブに（B は非アクティブ）

            window.RemoveItem(b, dispose: true);

            Assert.Equal(1, disposed);
            Assert.Equal(1, window.ItemCount);
            Assert.True(a.IsActive);   // 非アクティブ側を外したのでアクティブは動かない
        });
    }

    [Fact]
    public void 移動用に破棄なしで外すと破棄されない()
    {
        RunSta(() =>
        {
            var window = NewWindow();
            var disposed = 0;
            var a = NewItem("A");
            var b = NewItem("B", dispose: () => disposed++);
            window.AddItem(a);
            window.AddItem(b);

            window.RemoveItem(b, dispose: false);

            Assert.Equal(0, disposed);
            Assert.Equal(1, window.ItemCount);
            Assert.False(window.Contains(b));
        });
    }

    [Fact]
    public void 項目のDisposeは冪等()
    {
        RunSta(() =>
        {
            var count = 0;
            var item = NewItem("X", dispose: () => count++);
            item.Dispose();
            item.Dispose();
            Assert.Equal(1, count);
        });
    }

    [Fact]
    public void タブが増えてもキャプションボタンとドラッグ領域は帯に残る()
    {
        RunSta(() =>
        {
            var window = NewWindow();
            for (var i = 0; i < 12; i++)
                window.AddItem(NewItem($"とても長いタブのタイトル {i}"));

            const double barWidth = 500;
            window.TitleBar.Measure(new Size(barWidth, 34));
            window.TitleBar.Arrange(new Rect(0, 0, barWidth, 34));
            window.TitleBar.UpdateLayout();

            // タブ帯は残り幅で打ち切られ、あふれた分はスクロールへ回る（＝伸び続けない）。
            Assert.True(window.TabStripScrollViewer.ScrollableWidth > 0, "あふれた分はスクロールで送る");

            // キャプションボタンは帯の右端に収まり、タブ帯との間にはウィンドウ移動用の掴みしろが残る。
            var buttonsLeft = window.CaptionButtons.TransformToAncestor(window.TitleBar)
                .Transform(new Point(0, 0)).X;
            var stripRight = window.TabStripScrollViewer.TransformToAncestor(window.TitleBar)
                .Transform(new Point(window.TabStripScrollViewer.ActualWidth, 0)).X;

            Assert.Equal(barWidth - window.CaptionButtons.ActualWidth, buttonsLeft, precision: 3);
            Assert.True(buttonsLeft - stripRight >= 48, $"掴みしろが足りない（{buttonsLeft - stripRight}px）");
        });
    }

    [Fact]
    public void タブ一覧はタブ帯と同じ並びの全件を映す()
    {
        RunSta(() =>
        {
            var window = NewWindow();
            var a = NewItem("A");
            var b = NewItem("B");
            window.AddItem(a);
            window.AddItem(b);

            // 一覧は帯と同じ実体（_items）を直接映す＝あふれても件数も並びもズレない。
            Assert.Same(window.TabStripItems.ItemsSource, window.TabOverflowList.ItemsSource);
            Assert.Equal(new[] { a, b }, window.TabOverflowList.ItemsSource.Cast<DetachedItem>());

            window.RemoveItem(a, dispose: true);
            Assert.Equal(new[] { b }, window.TabOverflowList.ItemsSource.Cast<DetachedItem>());
        });
    }

    private static DetachedPaneWindow NewWindow()
        => new(new DetachedWindowManager(new Window()));

    private static DetachedItem NewItem(string title, Action? dispose = null)
        => new(DetachKind.EditorMirror, title, new Border(), icon: null, dispose: dispose);

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
