using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Services.Search;

namespace sk0ya.Loomo.Tests;

[Collection(WpfViewTests.Name)]
public sealed class TodoTreeViewTests(WpfViewHost host)
{
    [Fact]
    public async Task 実ビューの操作と全テーマの切替を確認する()
    {
        var root = Path.Combine(Path.GetTempPath(), $"todo-view-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "Example.cs"), "// TODO: 入力チェックを追加する\n// FIXME: 保存時のエラーを処理する\n// NOTE: 仕様の確認が必要");
        TodoTreeViewModel? vm = null;
        try
        {
            Task? refresh = null;
            host.Run(() => {
                var workspace = new FakeWorkspaceService(root);
                vm = new TodoTreeViewModel(new WorkspaceSearchService(workspace), workspace);
                refresh = vm.RefreshCommand.ExecuteAsync(null);
            });
            await refresh!;
            host.Run(() => {
                var view = new TodoTreeView { DataContext = vm, Width = 300, Height = 240 };
                foreach (var theme in Enum.GetValues<AppTheme>())
                {
                    var palette = new ResourceDictionary { Source = new Uri(
                        $"pack://application:,,,/sk0ya.Loomo.App;component/Themes/Palette.{theme}.xaml") };
                    view.Resources.MergedDictionaries.Clear();
                    view.Resources.MergedDictionaries.Add(palette);
                    Layout(view);
                    Assert.Equal(((SolidColorBrush)palette["BgAlt"]).Color, ((SolidColorBrush)view.Background).Color);
                    var tree = (TreeView)view.FindName("TodoTree");
                    var group = Assert.IsType<TreeViewItem>(tree.ItemContainerGenerator.ContainerFromIndex(0));
                    Assert.True(group.IsExpanded);
                    Assert.Equal(3, group.Items.Count);
                    Assert.False(vm!.IsFilterVisible);
                    if (theme is AppTheme.Dark or AppTheme.Light or AppTheme.HighContrast) Render(view, theme.ToString());
                }
                FindButton(view, "TODO をすべて折りたたむ").Command.Execute(null);
                Layout(view);
                Assert.False(vm!.Groups[0].IsExpanded);
                FindButton(view, "次の TODO").Command.Execute(null);
                Assert.Equal("TODO", vm.SelectedEntry!.Tag);
                Assert.True(vm.Groups[0].IsExpanded);
                FindButton(view, "TODO の絞り込みを表示").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(vm.IsFilterVisible);
                FindButton(view, "TODO の絞り込みを閉じる").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(vm.IsFilterVisible);
                vm.GroupByTag = true;
                Layout(view);
                Assert.Equal(3, vm.Groups.Count);
                Render(view, "Grouped");
                vm.Filter = "存在しない文言";
                Layout(view);
                Assert.True(vm.IsEmpty);
                Render(view, "Empty");
                vm.ClearFiltersCommand.Execute(null);
                vm.GroupByTag = false;
                view.Width = 220;
                view.Height = 200;
                Layout(view);
                Render(view, "Compact");
            });
        }
        finally
        {
            host.Run(() => vm?.Dispose());
            Directory.Delete(root, recursive: true);
        }
    }
    [Fact]
    public void 画面外の行への移動でも親を開いて選択する()
    {
        host.Run(() => {
            var workspace = new FakeWorkspaceService();
            using var vm = new TodoTreeViewModel(new WorkspaceSearchService(workspace), workspace);
            for (var i = 0; i < 100; i++)
            {
                var entry = new TodoEntry("TODO", new($"{i}.cs", $"{i}.cs", 1, 1, "TODO: sample"));
                vm.Groups.Add(new TodoGroup($"{i}.cs", [entry]) { IsExpanded = false });
            }
            var view = new TodoTreeView { DataContext = vm, Width = 220, Height = 200 };
            Layout(view);
            var target = vm.Groups[99].Entries[0];
            vm.SetSelection(target);
            view.FocusSelectedEntry();
            Layout(view);
            Assert.True(vm.Groups[99].IsExpanded);
            Assert.Same(target, ((TreeView)view.FindName("TodoTree")).SelectedItem);
        });
    }

    private static Button FindButton(DependencyObject root, string name)
        => Descendants(root).OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Layout(FrameworkElement view)
    {
        view.Measure(new Size(view.Width, view.Height));
        view.Arrange(new Rect(0, 0, view.Width, view.Height));
        view.UpdateLayout();
    }
    private static void Render(FrameworkElement view, string name)
    {
        var bitmap = new RenderTargetBitmap((int)view.Width, (int)view.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(Path.GetTempPath(), $"loomo-todo-v2-{name}.png"));
        encoder.Save(output);
    }
}
