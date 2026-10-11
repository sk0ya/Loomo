using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>NuGet パッケージ管理の窓（設計書 §36）。中身は <see cref="NuGetPackagesViewModel"/>。</summary>
public partial class NuGetPackagesWindow : Window
{
    public NuGetPackagesWindow(NuGetPackagesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            await viewModel.InitializeAsync();
        };
    }

    private void OnLogChanged(object sender, TextChangedEventArgs e)
        => ((TextBox)sender).ScrollToEnd();
}
