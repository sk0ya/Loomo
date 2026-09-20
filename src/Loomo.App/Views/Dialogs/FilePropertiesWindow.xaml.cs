using System.Collections.Generic;
using System.Windows;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>FolderTree の項目プロパティ。複数選択時は左の一覧で各項目を切り替えて確認できる。</summary>
public partial class FilePropertiesWindow : Window
{
    private readonly IReadOnlyList<FilePropertyItem> _items;

    public FilePropertiesWindow(FilePropertiesResult result)
    {
        InitializeComponent();
        _items = result.Items;
        SelectionText.Text = result.SelectionDisplay;
        ItemsList.ItemsSource = _items;
        ItemsList.SelectionChanged += OnItemSelectionChanged;
        PermissionsList.ItemsSource = Array.Empty<FilePermissionEntry>();
        if (_items.Count > 0)
            ItemsList.SelectedIndex = 0;
    }

    private void OnItemSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ItemsList.SelectedItem is not FilePropertyItem item)
            return;

        NameText.Text = item.Name;
        KindText.Text = item.KindDisplay;
        SizeText.Text = item.SizeDisplay;
        CreationText.Text = item.CreationTimeDisplay;
        LastWriteText.Text = item.LastWriteTimeDisplay;
        AttributesText.Text = item.AttributesDisplay;
        LocationText.Text = item.Location;
        PermissionsList.ItemsSource = item.Permissions;
        PermissionEmptyText.Visibility = item.Permissions.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        PermissionEmptyText.Text = item.PermissionError ?? "権限情報はありません。";
        ErrorText.Text = item.ErrorDisplay;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
