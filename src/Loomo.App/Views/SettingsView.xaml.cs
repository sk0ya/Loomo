using System.Windows.Controls;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>設定画面の中身（左カテゴリナビ＋右内容）。DataContext は <see cref="ShellViewModel"/>。
/// 独立ウィンドウ <see cref="SettingsWindow"/> に載せて使う（移動・リサイズは OS のウィンドウ操作に任せる）。
/// 閉じる／Esc の扱いはウィンドウ側の責務。</summary>
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
}
