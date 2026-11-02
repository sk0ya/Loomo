using System.Windows;
using System.Windows.Controls;

namespace sk0ya.Loomo.App.Views;

/// <summary>リポジトリを選ぶ部品（§40.8.3）。DataContext は <see cref="ViewModels.GitRepositoryStripViewModel"/>。</summary>
public partial class RepositoryStripView : UserControl
{
    public static readonly DependencyProperty IsInlineProperty = DependencyProperty.Register(
        nameof(IsInline), typeof(bool), typeof(RepositoryStripView),
        new PropertyMetadata(false, (d, _) => ((RepositoryStripView)d).ApplyInline()));

    public RepositoryStripView()
    {
        InitializeComponent();
        ApplyInline();
    }

    /// <summary>ヘッダー・ツールバーの中に置くか。置くなら切替の一言を箱の右に並べ、行の高さを変えない（既定は箱の下）。</summary>
    public bool IsInline
    {
        get => (bool)GetValue(IsInlineProperty);
        set => SetValue(IsInlineProperty, value);
    }

    private void ApplyInline()
    {
        Host.Orientation = IsInline ? Orientation.Horizontal : Orientation.Vertical;
        Picker.MinHeight = IsInline ? 22 : 26;
    }
}
