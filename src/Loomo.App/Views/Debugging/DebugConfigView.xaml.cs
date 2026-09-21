using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Services.Infrastructure;

namespace sk0ya.Loomo.App.Views;

/// <summary>構成タブ（起動構成・例外オプション・プロセスへのアタッチ・アダプタ未導入バー）。
/// DataContext は DebugViewModel（ファサード）。</summary>
public partial class DebugConfigView : UserControl
{
    private readonly DebugProfileEditorController _profileEditor;
    private readonly DebugAttachListController<DebugViewModel> _attachController;

    public DebugConfigView()
    {
        InitializeComponent();
        _attachController = new DebugAttachListController<DebugViewModel>(
            () => DataContext as DebugViewModel,
            vm => vm.Refresh(),
            vm => vm.Attach.AttachCommand);
        _profileEditor = new DebugProfileEditorController(
            () => (DataContext as DebugViewModel)?.Profiles,
            () => Window.GetWindow(this));
    }

    // アダプタ未導入バーの「再確認」：導入状況を取り直す。
    private void OnRefreshClick(object sender, RoutedEventArgs e) => _attachController.Refresh();

    // プロセス一覧のダブルクリック：その行のプロセスへ即アタッチ（行＝ListBoxItem 上のときだけ）。
    private void OnProcessDoubleClick(object sender, MouseButtonEventArgs e)
        => _attachController.AttachFromDoubleClick(e);

    // 構成の「+」：現在の設定を引き継いだ新しい構成を名前を聞いて追加する。
    private void OnAddProfileClick(object sender, RoutedEventArgs e) => _profileEditor.AddProfile();

    // 構成の「名前変更」：選択中の構成の名前を変える。
    private void OnRenameProfileClick(object sender, RoutedEventArgs e) => _profileEditor.RenameProfile();

    // 構成の「削除」：選択中の構成を削除する（最後の1件は消せない）。
    private void OnDeleteProfileClick(object sender, RoutedEventArgs e) => _profileEditor.DeleteProfile();
}
