using System.Windows.Controls;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>設定画面の中身（左カテゴリナビ＋右内容）。DataContext は <see cref="ShellViewModel"/>。
/// 独立ウィンドウ <see cref="SettingsWindow"/> に載せて使う（移動・リサイズは OS のウィンドウ操作に任せる）。
/// 閉じる／Esc の扱いはウィンドウ側の責務。キーボードショートカットは別ウィンドウ（<see cref="KeybindingsView"/>）。</summary>
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>「自分のテーマを作る・編集する」：専用の編集画面を開く（モードレス・1 枚だけ）。</summary>
    private void OnOpenUserThemeEditor(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel vm)
            UserThemeEditorView.Open(System.Windows.Window.GetWindow(this), vm.Appearance);
    }
}
