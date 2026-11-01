using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>リポジトリ一覧（§40.8.2）。DataContext は <see cref="RepositoryOverviewViewModel"/>。</summary>
public partial class RepositoryOverviewView : UserControl
{
    public RepositoryOverviewView() => InitializeComponent();

    private RepositoryOverviewViewModel? Vm => DataContext as RepositoryOverviewViewModel;

    /// <summary>見出しの文字 → 並べ替えの鍵。「ブランチ・状態」は変更の多い順に並べたい場面が多いので変更数で。</summary>
    private static string SortKeyFor(string header) => header switch
    {
        "ブランチ・状態" => "Changes",
        "最終取得" => "LastFetch",
        "場所" => "Path",
        _ => "Name",
    };

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Column.Header: string header })
            Vm?.SortBy(SortKeyFor(header));
    }

    private IReadOnlyList<RepositoryOverviewRow> Targets()
    {
        var selected = Table.SelectedItems.OfType<RepositoryOverviewRow>().ToList();
        return selected.Count > 0 ? selected : Vm?.Rows.ToList() ?? new List<RepositoryOverviewRow>();
    }

    private async void OnFetchClick(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.RunOnAsync(Targets(), pull: false);
    }

    private async void OnPullClick(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.RunOnAsync(Targets(), pull: true);
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.RefreshAsync();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseOverview();

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Table.SelectedItem is RepositoryOverviewRow row)
            Vm?.Open(row);
    }

    private void OnTableKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Table.SelectedItem is RepositoryOverviewRow row)
        {
            Vm?.Open(row);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseOverview();
            e.Handled = true;
        }
    }

    private void CloseOverview()
    {
        if (FindColumn() is { } column)
            column.CloseRepositoryOverview();
    }

    private FilesColumnViewModel? FindColumn()
    {
        for (DependencyObject? node = this; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is FilesColumnView { DataContext: FilesColumnViewModel column })
                return column;
        }
        return null;
    }
}
