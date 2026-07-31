using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Editor.Core.Lsp;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.Services.Lsp;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// LSP コード構造アウトラインのネイティブ WPF ビュー（<see cref="CodeOutlineView"/>）を実際に構築・表示して、
/// XAML の読み込み・暗黙 DataTemplate（②行/セクション）・HierarchicalDataTemplate（折りたたみツリー）・
/// バインドが起動時に壊れていないこと（＝クラッシュ検出）を確認する。ライブ LSP は不要で、ShellWindow が
/// 組む内部モデル（<see cref="OutlineNode"/>／<see cref="CallPanels"/>／<see cref="LspNoticeModel.Notice"/>）を
/// 直接流し込む。着地位置やハイライトの見た目そのものは対象外（それは要手動目視）。
/// </summary>
public class CodeOutlineViewTests
{
    [Fact]
    public void アウトラインクリックはシンボル名の行列を通知する()
    {
        RunSta(() =>
        {
            var view = new CodeOutlineView();
            var window = new Window { Width = 360, Height = 640, Content = view, ShowInTaskbar = false };
            try
            {
                SourceLocationActivatedEventArgs? activated = null;
                view.SourceLocationActivated += (_, e) => activated = e;
                view.ShowOutline(
                    new[] { new OutlineNode("Bar", SymbolKind.Method, 2, 5, 3, 12, Array.Empty<OutlineNode>()) },
                    currentLine1: 0,
                    CallPanels.Empty);
                window.Show();
                Pump();

                var rows = new List<Border>();
                CollectDescendants(view, rows);
                var row = Assert.Single(rows,
                    b => b.DataContext is CodeOutlineItem { Name: "Bar" }
                         && b.Cursor == System.Windows.Input.Cursors.Hand);
                row.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                {
                    RoutedEvent = System.Windows.Input.Mouse.MouseUpEvent,
                });

                Assert.NotNull(activated);
                Assert.Equal(4, activated!.Line1);
                Assert.Equal(12, activated.Column0);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void CurrentだけをLSPパネルと独立して更新できる()
    {
        RunSta(() =>
        {
            var vm = new CodeOutlineViewModel();
            vm.ShowOutline(
                new[]
                {
                    new OutlineNode("Foo", SymbolKind.Class, 0, 10, 0, 6,
                        new[]
                        {
                            new OutlineNode("Bar", SymbolKind.Method, 2, 5, 2, 4,
                                Array.Empty<OutlineNode>()),
                        }),
                },
                currentLine1: 1,
                CallPanels.Empty);

            var foo = Assert.Single(vm.Roots);
            var bar = Assert.Single(foo.Children);
            Assert.True(foo.IsCurrent);
            Assert.False(bar.IsCurrent);

            vm.SetCurrent(currentLine1: 3);

            Assert.False(foo.IsCurrent);
            Assert.True(bar.IsCurrent);
            Assert.All(vm.Sections, section => Assert.Empty(section.Rows));
        });
    }

    [Fact]
    public void ビュー_アウトラインと呼び出しパネルを構築できる()
    {
        RunSta(() =>
        {
            var view = new CodeOutlineView();
            var window = new Window { Width = 360, Height = 640, Content = view, ShowInTaskbar = false };
            try
            {
                window.Show();

                var roots = new[]
                {
                    new OutlineNode("Foo", SymbolKind.Class, 0, 10, 0, 6,
                        new[]
                        {
                            new OutlineNode("Bar", SymbolKind.Method, 2, 5, 2, 4,
                                System.Array.Empty<OutlineNode>(), "(int x)"),
                        }),
                };
                var panels = new CallPanels(
                    new[] { new CallReference("Caller", "file:///C:/work/Foo.cs", 41) },
                    System.Array.Empty<CallReference>(),
                    System.Array.Empty<CallReference>(),
                    "Bar");

                view.ShowOutline(roots, currentLine1: 3, panels); // Bar(Line0=2) → DataLine1=3 が current
                Pump();

                // 折りたたみツリーが実体化している（Foo＋Bar の 2 ノード以上）。
                Assert.True(CountDescendants<TreeViewItem>(view) >= 2);
                // ②パネルの行がセクションの DataTemplate で描かれている（「Foo.cs:42」＝1 始まり行）。
                Assert.Contains("Foo.cs:42", AllTextBlockText(view));
                // 見出し（対象シンボル）も出る。
                Assert.Contains("Bar の呼び出し関係", AllTextBlockText(view));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ビュー_未導入案内はインストールと設定ボタンを出す()
    {
        RunSta(() =>
        {
            var view = new CodeOutlineView();
            var window = new Window { Width = 360, Height = 640, Content = view, ShowInTaskbar = false };
            try
            {
                window.Show();

                var info = new LspPromptInfo(
                    ".rs", LspPromptKind.NotInstalled,
                    "「.rs」の言語サーバー rust-analyzer が見つかりません。",
                    "rustup component add rust-analyzer", "rust-analyzer", "https://example/docs");
                view.ShowNotice(LspNoticeModel.Build(info));
                Pump();

                var buttons = new List<Button>();
                CollectDescendants(view, buttons);
                // 非表示ボタンもツリーには残る（Visibility=Collapsed）ので、実際に見えているものだけで判定する。
                var shown = buttons.FindAll(b => b.IsVisible).ConvertAll(b => b.Content as string);
                Assert.Contains("インストール", shown);
                Assert.Contains("LSP 設定を開く", shown);
                Assert.DoesNotContain("導入手順を開く", shown); // コマンドがあるので手順ボタンは出さない
            }
            finally { window.Close(); }
        });
    }

    private static string AllTextBlockText(DependencyObject root)
    {
        var blocks = new List<TextBlock>();
        CollectDescendants(root, blocks);
        return string.Join("\n", blocks.ConvertAll(b => b.Text));
    }

    private static void CollectDescendants<T>(DependencyObject root, List<T> into) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) into.Add(t);
            CollectDescendants(child, into);
        }
    }

    private static int CountDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = 0;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T) count++;
            count += CountDescendants<T>(child);
        }
        return count;
    }

    private static void RunSta(Action body)
    {
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { ex = e; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (ex is not null) throw ex;
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
