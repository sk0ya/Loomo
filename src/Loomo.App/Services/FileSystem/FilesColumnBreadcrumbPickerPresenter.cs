using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.Services.Infrastructure;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>パンくずの階層選択ツリーとPopupの操作をまとめる。</summary>
internal sealed class FilesColumnBreadcrumbPickerPresenter
{
    private readonly Popup _popup;
    private readonly TreeView _tree;
    private readonly ScrollViewer _breadcrumbScroll;
    private readonly Func<FilesColumnViewModel?> _getViewModel;
    private string _selectionPath = "";
    private bool _pickerButtonPressed;

    internal FilesColumnBreadcrumbPickerPresenter(
        Popup popup,
        TreeView tree,
        ScrollViewer breadcrumbScroll,
        Func<FilesColumnViewModel?> getViewModel)
    {
        _popup = popup;
        _tree = tree;
        _breadcrumbScroll = breadcrumbScroll;
        _getViewModel = getViewModel;
    }

    internal void OnPickerMouseDown() => _pickerButtonPressed = true;

    internal void OnPickerMouseUp(MouseButtonEventArgs e)
    {
        if (!_pickerButtonPressed)
            e.Handled = true;
        _pickerButtonPressed = false;
    }

    internal void OnBreadcrumbScrollChanged(ScrollChangedEventArgs e)
    {
        if (e.ExtentWidthChange != 0 || e.ViewportWidthChange != 0)
            _breadcrumbScroll.ScrollToRightEnd();
    }

    internal void OnPickerClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FilesBreadcrumb breadcrumb } target
            || _getViewModel() is not { } vm)
            return;

        // キーボード操作など、Popupのマウスキャプチャを経由しない場合も同じトグルにする。
        if (_popup.IsOpen && ReferenceEquals(_popup.PlacementTarget, target))
        {
            _popup.IsOpen = false;
            e.Handled = true;
            return;
        }

        if (!Directory.Exists(breadcrumb.FullPath))
            return;

        var crumbIndex = vm.Breadcrumbs.IndexOf(breadcrumb);
        _selectionPath = crumbIndex >= 0 && crumbIndex + 1 < vm.Breadcrumbs.Count
            ? vm.Breadcrumbs[crumbIndex + 1].FullPath
            : "";
        _tree.Items.Clear();
        foreach (var path in FileSystemDirectoryQuery.EnumerateDirectories(breadcrumb.FullPath))
            _tree.Items.Add(CreateItem(path));

        if (_tree.Items.Count == 0)
            return;

        _popup.PlacementTarget = target;
        _popup.IsOpen = true;
        e.Handled = true;
    }

    internal void OnItemExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem item || item.Tag is not string path
            || item.Items.Count != 1 || item.Items[0] is not TreeViewItem { Tag: null })
            return;

        item.Items.Clear();
        foreach (var child in FileSystemDirectoryQuery.EnumerateDirectories(path))
            item.Items.Add(CreateItem(child));
        e.Handled = true;
    }

    internal void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || WpfTreeTraversal.FindVisualAncestor<ToggleButton>(source) is not null
            || WpfTreeTraversal.FindVisualAncestor<TreeViewItem>(source) is not { Tag: string path })
            return;

        _getViewModel()?.Navigate(path);
        _popup.IsOpen = false;
        e.Handled = true;
    }

    private TreeViewItem CreateItem(string path)
    {
        var item = new TreeViewItem
        {
            Header = CreateHeader(path),
            Tag = path,
            IsSelected = FilePathRelations.AreEqual(path, _selectionPath),
        };
        item.Expanded += OnItemExpanded;
        if (FileSystemDirectoryQuery.HasDirectories(path))
            item.Items.Add(new TreeViewItem { Tag = null, IsHitTestVisible = false });
        return item;
    }

    private static StackPanel CreateHeader(string path)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new Image
        {
            Source = FileIcons.FolderImage(open: false),
            Width = 14,
            Height = 14,
            Margin = new Thickness(1, 0, 5, 0),
        });
        header.Children.Add(new TextBlock
        {
            Text = FilePathRelations.FileNameOrPath(path),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return header;
    }
}
