using System.Linq;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.Views;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// エディタ右クリックメニューの<b>ネイティブ項目の取捨</b>（<c>ShellWindow.EditorNativeMenu</c>）。
/// ライブラリが組んだメニューがそのままホストへ渡ってくるので、Loomo は足すだけでなく
/// 外す・差し替えるところまでを担う。目印は <see cref="EditorMenuLabels"/> の見出しで、
/// ここが綴り違いになると「消したはずの項目が残る」形で静かに壊れる。
/// </summary>
public sealed class EditorNativeMenuTests
{
    private static ContextMenu NativeMenu() => new()
    {
        Items =
        {
            new MenuItem { Header = EditorMenuLabels.CopyLine },
            new MenuItem { Header = EditorMenuLabels.Paste },
            new MenuItem { Header = EditorMenuLabels.Undo },
            new MenuItem { Header = EditorMenuLabels.Redo },
            new MenuItem { Header = EditorMenuLabels.SelectAll },
            new MenuItem { Header = EditorMenuLabels.Navigate },
            new MenuItem { Header = EditorMenuLabels.CodeActions },
            new MenuItem { Header = EditorMenuLabels.FixAllInFile },
            new MenuItem { Header = EditorMenuLabels.HoverInfo },
        },
    };

    private static string[] Headers(ContextMenu menu)
        => menu.Items.OfType<MenuItem>().Select(item => (string)item.Header).ToArray();

    [Fact]
    public void 元に戻す_やり直す_すべて選択_だけを落とす()
        => RunSta(() =>
        {
            var menu = NativeMenu();

            ShellWindow.RemoveMenuItemsByHeader(menu, ShellWindow.DroppedNativeEditorMenuHeaders);

            Assert.Equal(
                [
                    EditorMenuLabels.CopyLine,
                    EditorMenuLabels.Paste,
                    EditorMenuLabels.Navigate,
                    EditorMenuLabels.CodeActions,
                    EditorMenuLabels.FixAllInFile,
                    EditorMenuLabels.HoverInfo,
                ],
                Headers(menu));
        });

    /// <summary>差し替えは<b>同じ位置</b>で行う。末尾へ足す実装にすると、LSP 操作の一群から
    /// 離れた場所に「Quick Fix」だけが現れる。</summary>
    [Fact]
    public void 差し替えは同じ位置に入る()
        => RunSta(() =>
        {
            var menu = NativeMenu();

            var replaced = ShellWindow.ReplaceMenuItemByHeader(
                menu, EditorMenuLabels.CodeActions, () => new MenuItem { Header = "Quick Fix" });

            Assert.True(replaced);
            Assert.Equal(
                [
                    EditorMenuLabels.CopyLine,
                    EditorMenuLabels.Paste,
                    EditorMenuLabels.Undo,
                    EditorMenuLabels.Redo,
                    EditorMenuLabels.SelectAll,
                    EditorMenuLabels.Navigate,
                    "Quick Fix",
                    EditorMenuLabels.FixAllInFile,
                    EditorMenuLabels.HoverInfo,
                ],
                Headers(menu));
        });

    /// <summary>見出しが違えば（＝古い Editor で英語のままなら）何もしない。
    /// 「見つからないなら黙って差し替えない」が、ネイティブ項目を壊さない条件。</summary>
    [Fact]
    public void 見出しが無ければ何もしない()
        => RunSta(() =>
        {
            var menu = new ContextMenu { Items = { new MenuItem { Header = "Code Actions" } } };

            var replaced = ShellWindow.ReplaceMenuItemByHeader(
                menu, EditorMenuLabels.CodeActions, () => new MenuItem { Header = "Quick Fix" });

            Assert.False(replaced);
            Assert.Equal(["Code Actions"], Headers(menu));
        });

    /// <summary>区切り線など <see cref="MenuItem"/> 以外は触らない
    /// （束の区切りはライブラリ側の <c>TrimMenuSeparators</c> が最後に整える）。</summary>
    [Fact]
    public void 区切り線は落とさない()
        => RunSta(() =>
        {
            var menu = new ContextMenu
            {
                Items =
                {
                    new MenuItem { Header = EditorMenuLabels.Undo },
                    new Separator(),
                    new MenuItem { Header = EditorMenuLabels.Paste },
                },
            };

            ShellWindow.RemoveMenuItemsByHeader(menu, ShellWindow.DroppedNativeEditorMenuHeaders);

            Assert.Single(menu.Items.OfType<Separator>());
            Assert.Equal([EditorMenuLabels.Paste], Headers(menu));
        });

    private static void RunSta(System.Action body)
    {
        System.Exception? error = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (System.Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw error;
    }
}
