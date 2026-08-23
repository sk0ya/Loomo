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
public sealed class FilesColumnViewLayoutTests
{
    [Fact]
    public void 表示形式6つが実レイアウトとComboBoxへ反映される()
    {
        RunSta(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"loomo-files-layout-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "alpha.txt"), "alpha");
            Directory.CreateDirectory(Path.Combine(root, "folder"));

            var workspace = new FakeWorkspaceService();
            workspace.OpenFolder(root);
            var tree = new FolderTreeViewModel(workspace, new FakeAiWarmup(),
                new WorkflowStore(Path.Combine(Path.GetTempPath(), $"loomo-layout-workflows-{Guid.NewGuid():N}")),
                new FolderTreeCommandHandler(workspace, new FileOperationHistory()), new FolderTreeQuery());
            tree.LoadRoot(root);
            var column = new FilesColumnViewModel(
                workspace, FolderTreeCommandHandler.Unconfined(workspace, new FileOperationHistory()),
                tree, new FakeFilePlacesProvider());
            column.Restore(snapshot: null, fallbackFolder: root);

            try
            {
                if (Application.Current is null)
                {
                    var app = new sk0ya.Loomo.App.App();
                    app.InitializeComponent();
                }
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
                    var combo = FindDescendant<ComboBox>(view);
                    Assert.NotNull(list);
                    Assert.NotNull(combo);
                    Assert.Equal(6, combo!.Items.Count);
                    Assert.Equal(SelectionMode.Extended, list!.SelectionMode);
                    Assert.NotNull(list.ContextMenu);
                    list.SelectedIndex = 0;
                    var selected = list.SelectedItem;
                    Assert.NotNull(selected);

                    foreach (var mode in FilesDisplayModes.Options.Select(option => option.Value))
                    {
                        column.DisplayMode = mode;
                        window.UpdateLayout();

                        Assert.Equal(mode, combo.SelectedValue);
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

                        if (mode is FilesDisplayMode.Details or FilesDisplayMode.List)
                            Assert.Null(FindDescendant<WrapPanel>(list));
                        else
                            Assert.NotNull(FindDescendant<WrapPanel>(list));
                    }

                    combo.SelectedValue = FilesDisplayMode.Tiles;
                    window.UpdateLayout();
                    Assert.Equal(FilesDisplayMode.Tiles, column.DisplayMode);

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

    private static void RunSta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw error;
    }
}
