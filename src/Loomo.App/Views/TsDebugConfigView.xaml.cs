using System.Windows;
using System.Windows.Controls;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>TS 構成タブ（起動構成・例外オプション・ポートへのアタッチ・アダプタ未導入バー）。
/// DataContext は TsDebugViewModel（ファサード）。</summary>
public partial class TsDebugConfigView : UserControl
{
    public TsDebugConfigView() => InitializeComponent();

    // アダプタ未導入バーの「再確認」：導入状況を取り直す。
    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TsDebugViewModel vm) vm.Refresh();
    }

    // 構成の「+」：現在の設定を引き継いだ新しい構成を名前を聞いて追加する。
    private void OnAddProfileClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TsDebugViewModel vm) return;
        var name = InputDialog.Prompt(Window.GetWindow(this), "新しい構成", "構成名を入力してください:");
        if (!string.IsNullOrWhiteSpace(name)) vm.Profiles.AddProfile(name);
    }

    // 構成の「名前変更」：選択中の構成の名前を変える。
    private void OnRenameProfileClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TsDebugViewModel vm || vm.Profiles.SelectedProfile is null) return;
        var name = InputDialog.Prompt(Window.GetWindow(this), "構成名の変更", "構成名を入力してください:",
            vm.Profiles.SelectedProfile.Name);
        if (!string.IsNullOrWhiteSpace(name)) vm.Profiles.RenameSelectedProfile(name);
    }

    // 構成の「削除」：選択中の構成を削除する（最後の1件は消せない）。
    private void OnDeleteProfileClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TsDebugViewModel vm) vm.Profiles.DeleteSelectedProfile();
    }
}
