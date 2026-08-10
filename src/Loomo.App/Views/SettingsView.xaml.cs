using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>設定画面の中身（左カテゴリナビ＋右内容）。DataContext は <see cref="ShellViewModel"/>。
/// 独立ウィンドウ <see cref="SettingsWindow"/> に載せて使う（移動・リサイズは OS のウィンドウ操作に任せる）。
/// 閉じる／Esc の扱いはウィンドウ側の責務。</summary>
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>キーボードカテゴリを開いたら検索ボックスにフォーカスを置く。40 件超の一覧なので、
    /// 開いた直後にやりたいことはほぼ「目的のコマンドを探す」＝すぐ打ち始められるようにする。
    /// 表示直後はまだレイアウトが済んでいないので、フォーカスは 1 フレーム遅らせる。</summary>
    private void OnKeyboardSectionVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input,
            () => KeybindingSearchBox.Focus());
    }
}
