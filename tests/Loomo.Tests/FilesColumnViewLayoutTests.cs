using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.Core.Agent;

namespace sk0ya.Loomo.Tests;

/// <summary>ファイル一覧の表示形式を実際のWPFビューへ適用したときのレイアウト検証。</summary>
[Collection(WpfViewTests.Name)]
public sealed class FilesColumnViewLayoutTests
{
    // ビューは共有の STA ホスト上で組み立てる（WpfViewTests のコレクション）。
    private readonly WpfViewHost _host;

    public FilesColumnViewLayoutTests(WpfViewHost host) => _host = host;

    [Fact]
    public void 表示形式6つが実レイアウトと表示形式ポップアップへ反映される()
    {
        RunSta(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"loomo-files-layout-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "alpha.txt"), "alpha");
            File.WriteAllText(Path.Combine(root, "photo.png"), "test");
            Directory.CreateDirectory(Path.Combine(root, "folder"));

            var workspace = new FakeWorkspaceService();
            workspace.OpenFolder(root);
            var tree = new FolderTreeViewModel(workspace, new FakeAiWarmup(),
                new WorkflowStore(Path.Combine(Path.GetTempPath(), $"loomo-layout-workflows-{Guid.NewGuid():N}")),
                new FolderTreeCommandHandler(workspace, new FileOperationHistory()), new FolderTreeQuery());
            tree.LoadRoot(root);
            var thumbnailService = new FixedThumbnailService();
            var column = new FilesColumnViewModel(
                workspace, FolderTreeCommandHandler.Unconfined(workspace, new FileOperationHistory()),
                tree, new FakeFilePlacesProvider(), thumbnails: thumbnailService);
            column.Restore(snapshot: null, fallbackFolder: root);

            try
            {
                var view = new FilesColumnView { DataContext = column };
                var window = new Window
                {
                    Width = 640,
                    Height = 420,
                    Content = view,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None
                };
                try
                {
                    window.Show();
                    window.UpdateLayout();

                    var list = FindDescendant<ListBox>(view);
                    Assert.NotNull(list);

                    // 表示形式は素の ComboBox ではなく、ペインと同じ配色のポップアップから選ぶ。
                    var displayButton = view.FindName("DisplayModeButton") as ToggleButton;
                    var displayPopup = view.FindName("DisplayModePopup") as Popup;
                    Assert.NotNull(displayButton);
                    Assert.NotNull(displayPopup);
                    displayButton!.IsChecked = true;
                    window.UpdateLayout();
                    Assert.True(displayPopup!.IsOpen);
                    var modeButtons = FindDescendants<Button>(displayPopup.Child!).ToList();
                    Assert.Equal(6, modeButtons.Count);
                    Assert.Equal(SelectionMode.Extended, list!.SelectionMode);
                    Assert.NotNull(list.ContextMenu);
                    list.SelectedIndex = 0;
                    var selected = list.SelectedItem;
                    Assert.NotNull(selected);

                    foreach (var mode in FilesDisplayModes.Options.Select(option => option.Value))
                    {
                        column.DisplayMode = mode;
                        window.UpdateLayout();

                        Assert.Same(selected, list.SelectedItem);
                        var item = FindDescendant<ListBoxItem>(list);
                        Assert.NotNull(item);

                        var expectedName = mode switch
                        {
                            FilesDisplayMode.Details => "DetailsLayout",
                            FilesDisplayMode.List => "ListLayout",
                            FilesDisplayMode.LargeIcons => "LargeIconsLayout",
                            FilesDisplayMode.MediumIcons => "MediumIconsLayout",
                            FilesDisplayMode.SmallIcons => "SmallIconsLayout",
                            FilesDisplayMode.Tiles => "TilesLayout",
                            _ => throw new InvalidOperationException()
                        };
                        var layout = FindNamed(item!, expectedName);
                        Assert.NotNull(layout);
                        Assert.Equal(Visibility.Visible, layout!.Visibility);

                        if (mode is FilesDisplayMode.LargeIcons or FilesDisplayMode.MediumIcons
                            or FilesDisplayMode.SmallIcons or FilesDisplayMode.Tiles)
                        {
                            var photo = column.Entries.Single(entry => entry.Name == "photo.png");
                            var photoItem = list.ItemContainerGenerator.ContainerFromItem(photo) as ListBoxItem;
                            Assert.NotNull(photoItem);
                            var photoLayout = FindNamed(photoItem!, expectedName);
                            var photoImage = FindDescendant<Image>(photoLayout!);
                            Assert.NotNull(photoImage);
                            Assert.Same(thumbnailService.Image, photoImage!.Source);
                        }

                        if (mode is FilesDisplayMode.Details or FilesDisplayMode.List)
                            Assert.Null(FindDescendant<WrapPanel>(list));
                        else
                            Assert.NotNull(FindDescendant<WrapPanel>(list));
                    }

                    // ポップアップの行を押すと表示形式が切り替わり、ポップアップは閉じる。
                    var tilesButton = modeButtons[
                        FilesDisplayModes.Options.Select(option => option.Value).ToList()
                            .IndexOf(FilesDisplayMode.Tiles)];
                    tilesButton.Command.Execute(tilesButton.CommandParameter);
                    window.UpdateLayout();
                    Assert.Equal(FilesDisplayMode.Tiles, column.DisplayMode);
                    Assert.False(column.IsDisplayMenuOpen);

                    var columnsButton = view.FindName("ColumnsButton") as ToggleButton;
                    var popup = view.FindName("ColumnSettingsPopup") as Popup;
                    Assert.NotNull(columnsButton);
                    Assert.NotNull(popup);
                    columnsButton!.IsChecked = true;
                    window.UpdateLayout();
                    Assert.True(popup!.IsOpen);
                    Assert.NotNull(popup.Child);

                    var checks = FindDescendants<CheckBox>(popup.Child!).ToList();
                    Assert.Equal(4, checks.Count);
                    Assert.Contains(checks, check => check.Content?.ToString() == "名前" && !check.IsEnabled);
                    var sizeCheck = Assert.Single(checks, check => check.Content?.ToString() == "サイズ");
                    sizeCheck.IsChecked = false;
                    window.UpdateLayout();
                    Assert.False(column.IsSizeColumnVisible);

                    sizeCheck.IsChecked = true;
                    var nameSetting = column.ColumnSettings.Single(setting => setting.Key == FilesColumnKey.Name);
                    var down = Assert.Single(FindDescendants<Button>(popup.Child!),
                        button => button.Content?.ToString() == "↓"
                            && ReferenceEquals(button.CommandParameter, nameSetting));
                    Assert.NotNull(down.Command);
                    down.Command!.Execute(down.CommandParameter);
                    window.UpdateLayout();
                    Assert.Equal(FilesColumnKey.Size, column.ColumnSettings[0].Key);
                    Assert.Equal(1, column.NameColumnIndex);

                    column.DisplayMode = FilesDisplayMode.Details;
                    column.SetColumnWidth(FilesColumnKey.Name, 333);
                    window.UpdateLayout();
                    var detail = FindNamed(view, "DetailsLayout") as Grid;
                    Assert.NotNull(detail);
                    Assert.Equal(333, detail!.ColumnDefinitions[column.NameColumnIndex].ActualWidth, 0);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                column.Dispose();
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        });
    }

    [Fact]
    public void 列の境目のつまみで幅を変えられる()
    {
        RunSta(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"loomo-files-grip-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "alpha.txt"), "alpha");

            var workspace = new FakeWorkspaceService();
            workspace.OpenFolder(root);
            var tree = new FolderTreeViewModel(workspace, new FakeAiWarmup(),
                new WorkflowStore(Path.Combine(Path.GetTempPath(), $"loomo-grip-workflows-{Guid.NewGuid():N}")),
                new FolderTreeCommandHandler(workspace, new FileOperationHistory()), new FolderTreeQuery());
            tree.LoadRoot(root);
            var column = new FilesColumnViewModel(
                workspace, FolderTreeCommandHandler.Unconfined(workspace, new FileOperationHistory()),
                tree, new FakeFilePlacesProvider());
            column.Restore(snapshot: null, fallbackFolder: root);

            try
            {
                var view = new FilesColumnView { DataContext = column };
                var window = new Window
                {
                    Width = 900,
                    Height = 420,
                    Content = view,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None
                };
                try
                {
                    window.Show();
                    window.UpdateLayout();

                    // 幅のつまみは列ごとに1つ。場所パネルの GridSplitter も Thumb なので Tag で選り分ける。
                    var grips = FindDescendants<Thumb>(view)
                        .Where(thumb => thumb.Tag is string tag && Enum.TryParse<FilesColumnKey>(tag, out _))
                        .ToList();
                    Assert.Equal(4, grips.Count);

                    var nameGrip = Assert.Single(grips, thumb => (string)thumb.Tag == nameof(FilesColumnKey.Name));
                    var header = (Grid)VisualTreeHelper.GetParent(nameGrip);

                    // つまみの中心は列の境目そのもの（見出しの右端 8px ではない）。
                    var center = nameGrip.TransformToAncestor(header)
                        .Transform(new Point(nameGrip.ActualWidth / 2, 0)).X;
                    Assert.True(Math.Abs(center - column.ColumnWidth(FilesColumnKey.Name)) <= 1,
                        $"つまみの中心 {center} が列の境目 {column.ColumnWidth(FilesColumnKey.Name)} から離れている");

                    var saves = 0;
                    column.StateChanged += (_, _) => saves++;

                    nameGrip.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                    nameGrip.RaiseEvent(new DragDeltaEventArgs(40, 0) { RoutedEvent = Thumb.DragDeltaEvent });
                    nameGrip.RaiseEvent(new DragDeltaEventArgs(20, 0) { RoutedEvent = Thumb.DragDeltaEvent });
                    window.UpdateLayout();

                    Assert.Equal(300, column.ColumnWidth(FilesColumnKey.Name));
                    var detail = (Grid)FindNamed(view, "DetailsLayout")!;
                    Assert.Equal(300, detail.ColumnDefinitions[column.NameColumnIndex].ActualWidth, 0);
                    Assert.Equal(0, saves);   // 掴んでいる間は保存しない

                    nameGrip.RaiseEvent(new DragCompletedEventArgs(60, 0, false)
                    {
                        RoutedEvent = Thumb.DragCompletedEvent
                    });
                    Assert.Equal(1, saves);

                    // 下限より狭くはできない（列が潰れて名前が読めなくなるのを防ぐ）。
                    nameGrip.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                    nameGrip.RaiseEvent(new DragDeltaEventArgs(-500, 0) { RoutedEvent = Thumb.DragDeltaEvent });
                    nameGrip.RaiseEvent(new DragCompletedEventArgs(-500, 0, false)
                    {
                        RoutedEvent = Thumb.DragCompletedEvent
                    });
                    Assert.Equal(120, column.ColumnWidth(FilesColumnKey.Name));

                    // 非表示の列のつまみは出ない（境目がないところに掴めるものを置かない）。
                    column.ColumnSettings.Single(setting => setting.Key == FilesColumnKey.Size).IsVisible = false;
                    window.UpdateLayout();
                    var sizeGrip = Assert.Single(grips, thumb => (string)thumb.Tag == nameof(FilesColumnKey.Size));
                    Assert.Equal(Visibility.Collapsed, sizeGrip.Visibility);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                column.Dispose();
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 幅を内容に合わせた列は見切れない(bool 大きい文字)
    {
        // UI の文字サイズは設定で変わる（§UIフォント）。実測に使う Fs* を焼き込むと、
        // 大きくしたときだけ測り足りず「合わせたのに三点リーダーが出る」になる。
        RunSta(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"loomo-files-fit-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "とても長い名前のフォルダー"));
            File.WriteAllText(Path.Combine(root, "とても長いファイル名のサンプル文書.md"), new string('x', 5000));
            File.WriteAllText(Path.Combine(root, "a.txt"), "a");

            var workspace = new FakeWorkspaceService();
            workspace.OpenFolder(root);
            var tree = new FolderTreeViewModel(workspace, new FakeAiWarmup(),
                new WorkflowStore(Path.Combine(Path.GetTempPath(), $"loomo-fit-workflows-{Guid.NewGuid():N}")),
                new FolderTreeCommandHandler(workspace, new FileOperationHistory()), new FolderTreeQuery());
            tree.LoadRoot(root);
            var column = new FilesColumnViewModel(
                workspace, FolderTreeCommandHandler.Unconfined(workspace, new FileOperationHistory()),
                tree, new FakeFilePlacesProvider());
            column.Restore(snapshot: null, fallbackFolder: root);

            try
            {
                var view = new FilesColumnView { DataContext = column };
                if (大きい文字)
                {
                    // UiFontManager が基準pxから配り直すのと同じ形で、この木だけ大きくする。
                    // 倍率は既定（16px ÷ 基準13px ≒ 1.23）——つまり素の Typography.xaml の値
                    // （11・12）で測るのは、既定の設定ですら足りない。
                    var scale = UiFontManager.DefaultSize / UiFontManager.ReferenceSize;
                    view.Resources["Fs10"] = Math.Round(10 * scale, 2);
                    view.Resources["Fs11"] = Math.Round(11 * scale, 2);
                    view.Resources["Fs12"] = Math.Round(12 * scale, 2);
                }
                var window = new Window
                {
                    Width = 1200,
                    Height = 420,
                    Content = view,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None
                };
                try
                {
                    window.Show();
                    window.UpdateLayout();

                    foreach (var key in new[]
                    {
                        FilesColumnKey.Name, FilesColumnKey.Size,
                        FilesColumnKey.Modified, FilesColumnKey.Type
                    })
                    {
                        view.AutoFitColumn(key);
                    }
                    window.UpdateLayout();

                    var list = FindDescendant<ListBox>(view)!;
                    var rows = 0;
                    foreach (var entry in column.Entries)
                    {
                        var item = list.ItemContainerGenerator.ContainerFromItem(entry) as ListBoxItem;
                        Assert.NotNull(item);
                        var details = (Grid)FindNamed(item!, "DetailsLayout")!;
                        foreach (var text in FindDescendants<TextBlock>(details))
                        {
                            if (text.Visibility != Visibility.Visible || string.IsNullOrEmpty(text.Text))
                                continue;
                            // 三点リーダーが出るのは「置ける幅 < 文字の幅」のとき。
                            var natural = new FormattedText(text.Text, CultureInfo.CurrentCulture,
                                FlowDirection.LeftToRight,
                                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
                                text.FontSize, Brushes.Black,
                                VisualTreeHelper.GetDpi(text).PixelsPerDip).WidthIncludingTrailingWhitespace;
                            Assert.True(text.ActualWidth + 0.5 >= natural,
                                $"「{text.Text}」が {text.ActualWidth} に収まらない（必要 {natural}・大きい文字={大きい文字}）");
                            rows++;
                        }
                    }
                    Assert.True(rows > 0);

                    // 絞り込み中は残っている行に合わせる（消えている長い名前まで見込むと、
                    // 絞り込んだ結果ほど列だけ広いままになる）。
                    var 全体 = column.ColumnWidth(FilesColumnKey.Name);
                    column.Filter = "a.txt";
                    window.UpdateLayout();
                    view.AutoFitColumn(FilesColumnKey.Name);
                    Assert.True(column.ColumnWidth(FilesColumnKey.Name) < 全体);
                    column.Filter = "";
                    window.UpdateLayout();

                    // 見出しも欠けない（並べ替え記号ぶんも見込む）。
                    var header = FindDescendants<Button>(view)
                        .Single(button => (button.Tag as string) == nameof(FilesColumnKey.Type));
                    var label = FindDescendant<TextBlock>(header)!;
                    Assert.True(label.ActualWidth > 0);
                    Assert.True(header.ActualWidth >= label.ActualWidth);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                column.Dispose();
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        });
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is { } nested)
                return nested;
        }
        return null;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var nested in FindDescendants<T>(child))
                yield return nested;
        }
    }

    private static FrameworkElement? FindNamed(DependencyObject root, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element && element.Name == name)
                return element;
            if (FindNamed(child, name) is { } nested)
                return nested;
        }
        return null;
    }

    private void RunSta(Action body) => _host.Run(body);

    private sealed class FixedThumbnailService : IFileThumbnailService
    {
        public FixedThumbnailService()
        {
            var drawing = new DrawingGroup();
            drawing.Freeze();
            Image = new DrawingImage(drawing);
            Image.Freeze();
        }

        public ImageSource Image { get; }

        public Task<ImageSource?> GetThumbnailAsync(string path, int edge, CancellationToken cancellationToken = default)
            => Task.FromResult<ImageSource?>(Image);
    }
}
