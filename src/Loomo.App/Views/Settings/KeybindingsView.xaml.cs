using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>キーボードショートカット画面の中身（左＝大分類ナビ、右＝絞り込み道具＋一覧）。
/// DataContext は <see cref="KeybindingsViewModel"/>。設定画面とは別の独立ウィンドウ（<see cref="SettingsWindow"/>
/// を枠として流用）に載せる。閉じる／Esc の扱いはウィンドウ側の責務。</summary>
public partial class KeybindingsView : UserControl
{
    public KeybindingsView() => InitializeComponent();

    /// <summary>開いたら検索ボックスにフォーカスを置く。開いた直後にやりたいことはほぼ「目的のコマンドを探す」
    /// ＝すぐ打ち始められるようにする。表示直後はまだレイアウトが済んでいないので、フォーカスは 1 フレーム遅らせる。</summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
        => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => KeybindingSearchBox.Focus());
}
