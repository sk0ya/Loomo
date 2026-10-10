using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.Tests;

/// <summary>「問題」一覧の抜粋タブの入口（§35.3）が実ビューで VM のコマンドへ届くことを見る。
/// ContextMenu はビジュアルツリーの外なので、Tag 経由のバインドが切れていても XAML は黙って通る——
/// 実際にメニューを開いて項目のコマンドが結線されていることを確かめる。</summary>
[Collection(WpfViewTests.Name)]
public sealed class DebugProblemsExcerptMenuTests
{
    private readonly WpfViewHost _host;

    public DebugProblemsExcerptMenuTests(WpfViewHost host) => _host = host;

    [Fact]
    public void 行と見出しの右クリックと件数行のボタンが抜粋の要求を出す()
    {
        _host.Run(() =>
        {
            var vm = new ProblemsViewModel();
            vm.SetFromBuildOutput(string.Join("\n",
                @"C:\src\A.cs(3,5): error CS8618: null 非許容 [C:\src\P.csproj]",
                @"C:\src\B.cs(4,1): error CS8618: null 非許容 [C:\src\P.csproj]"));
            var titles = new List<string>();
            vm.ExcerptRequested += plan => titles.Add(plan.Title);

            var view = new DebugProblemsView { DataContext = vm };
            var window = new Window
            {
                Width = 640, Height = 420, Content = view, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
            };
            window.Show();
            try
            {
                window.UpdateLayout();

                var button = Descendants<Button>(view).Single(b => b.Content as string == "▤ 抜粋タブ");
                Assert.True(button.IsVisible);
                button.Command.Execute(button.CommandParameter);

                // 見出し（ProblemFileGroup）と診断行（ProblemItemViewModel）のメニュー。
                var tree = (TreeView)view.FindName("Tree")!;
                var groupItem = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0);
                groupItem.IsExpanded = true;
                window.UpdateLayout();
                var groupMenu = OpenMenuOf<ProblemFileGroup>(groupItem);
                var rowItem = (TreeViewItem)groupItem.ItemContainerGenerator.ContainerFromIndex(0);
                var rowMenu = OpenMenuOf<ProblemItemViewModel>(rowItem);

                Assert.Equal(
                    ["CS8618 をすべて抜粋タブで開く", "このファイルの問題を抜粋タブで開く", "表示中の問題をすべて抜粋タブで開く"],
                    rowMenu.Items.Cast<MenuItem>().Select(m => (string)m.Header).ToArray());
                foreach (var menu in new[] { groupMenu, rowMenu })
                {
                    foreach (var item in menu.Items.Cast<MenuItem>())
                    {
                        Assert.NotNull(item.Command);   // Tag 経由のバインドが切れていない
                        item.Command.Execute(item.CommandParameter);
                    }
                    menu.IsOpen = false;
                }

                Assert.Equal(
                    ["問題: 表示中 (2)",
                     "問題: A.cs (1)", "問題: 表示中 (2)",
                     "問題: CS8618 (2)", "問題: A.cs (1)", "問題: 表示中 (2)"],
                    titles);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static ContextMenu OpenMenuOf<T>(TreeViewItem container)
    {
        var target = Descendants<Grid>(container).First(g => g.DataContext is T && g.ContextMenu is not null);
        var menu = target.ContextMenu!;
        menu.PlacementTarget = target;
        menu.IsOpen = true;
        menu.UpdateLayout();
        return menu;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
