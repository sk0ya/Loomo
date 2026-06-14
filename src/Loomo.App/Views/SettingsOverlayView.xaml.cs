using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>中央オーバーレイの設定画面（左カテゴリナビ＋右内容）。DataContext は <see cref="ShellViewModel"/>。
/// 背景クリック／Esc／閉じるボタンで閉じる。表示時にフォーカスを受け取り Esc を拾えるようにする。</summary>
public partial class SettingsOverlayView : UserControl
{
    public SettingsOverlayView()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // 開いた直後にフォーカスを移し、Esc（PreviewKeyDown）を確実に拾えるようにする。
        if (IsVisible)
            Focus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // キーキャプチャ中（KeyCaptureBox にフォーカス）のキーは横取りしない。Esc も取消に使うため委ねる。
        if (IsWithinCaptureBox(e.OriginalSource as DependencyObject))
            return;

        if (e.Key == Key.Escape && DataContext is ShellViewModel vm)
        {
            vm.CloseSettingsOverlayCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static bool IsWithinCaptureBox(DependencyObject? source)
    {
        for (var node = source; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (node is KeyCaptureBox)
                return true;
        return false;
    }

    /// <summary>背景（薄暗がり）クリックで閉じる。</summary>
    private void OnBackdropMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ShellViewModel vm)
            vm.CloseSettingsOverlayCommand.Execute(null);
    }

    /// <summary>パネル本体のクリックは背景まで抜けさせない。</summary>
    private void OnPanelMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
