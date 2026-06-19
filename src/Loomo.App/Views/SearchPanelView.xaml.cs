using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

public partial class SearchPanelView : UserControl
{
    public SearchPanelView() => InitializeComponent();

    private SearchPanelViewModel? Vm => DataContext as SearchPanelViewModel;

    /// <summary>一致行（grep）やファイル名ヒットを選択したらエディタでプレビューする（単クリック・矢印キー移動）。
    /// grep のファイル見出し（一致行を子に持つグループ）は展開用なのでプレビューしない。</summary>
    private void OnResultSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        switch (e.NewValue)
        {
            case SearchMatchItem match:
                Vm?.Preview(match);
                break;
            case SearchFileGroup group when group.Count == 0: // ファイル名ヒット
                Vm?.Preview(group);
                break;
        }
    }

    /// <summary>ダブルクリックで通常タブへ昇格（プレビューでなく確定して開く）。</summary>
    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultTree.SelectedItem is SearchMatchItem match)
        {
            Vm?.Activate(match);
            e.Handled = true;
        }
        else if (ResultTree.SelectedItem is SearchFileGroup group && group.Count == 0)
        {
            Vm?.Activate(group);
            e.Handled = true;
        }
    }

    private void OnResultKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        if (ResultTree.SelectedItem is SearchMatchItem match)
        {
            Vm?.Activate(match);
            e.Handled = true;
        }
        else if (ResultTree.SelectedItem is SearchFileGroup group && group.Count == 0)
        {
            Vm?.Activate(group);
            e.Handled = true;
        }
    }

    /// <summary>クエリ欄で Enter を押したら先頭ファイルへフォーカスを移す（結果を辿りやすく）。</summary>
    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && ResultTree.Items.Count > 0)
        {
            ResultTree.Focus();
            if (ResultTree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem first)
                first.IsSelected = true;
            e.Handled = true;
        }
    }
}
