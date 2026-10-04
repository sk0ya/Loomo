using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services.Search;

namespace sk0ya.Loomo.Tests;

[Collection(WpfViewTests.Name)]
public sealed class TodoTreeViewTests(WpfViewHost host)
{
    [Fact]
    public void 実際のビューを組み立てテーマ切替を即時反映する()
    {
        host.Run(() =>
        {
            using var vm = new TodoTreeViewModel(new WorkspaceSearchService(new FakeWorkspaceService()), new FakeWorkspaceService());
            vm.Groups.Add(new TodoGroup("src/Example.cs", [
                new("TODO", new("src/Example.cs", "src/Example.cs", 12, 4, "// TODO: 入力チェックを追加する")),
                new("FIXME", new("src/Example.cs", "src/Example.cs", 28, 4, "// FIXME: 保存時のエラーを処理する"))]));
            var view = new TodoTreeView { DataContext = vm, Width = 340, Height = 300 };
            Color? previous = null;
            foreach (var theme in new[] { "Dark", "Light" })
            {
                var palette = new ResourceDictionary { Source = new Uri(
                    $"pack://application:,,,/sk0ya.Loomo.App;component/Themes/Palette.{theme}.xaml") };
                view.Resources.MergedDictionaries.Clear();
                view.Resources.MergedDictionaries.Add(palette);
                view.Measure(new Size(340, 300));
                view.Arrange(new Rect(0, 0, 340, 300));
                view.UpdateLayout();
                var color = ((SolidColorBrush)view.Background).Color;
                Assert.Equal(((SolidColorBrush)palette["BgAlt"]).Color, color);
                if (previous is { } old) Assert.NotEqual(old, color);
                previous = color;
                var tree = (TreeView)view.FindName("TodoTree");
                Assert.Single(tree.Items.Cast<object>());
                var group = Assert.IsType<TreeViewItem>(tree.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.True(group.IsExpanded);
                Assert.Equal(2, group.Items.Count);
                var bitmap = new RenderTargetBitmap(340, 300, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(Path.GetTempPath(), $"loomo-todo-{theme}.png"));
                encoder.Save(output);
            }
        });
    }
}
