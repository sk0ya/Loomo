using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// Diff セッションペイン。AI変更（ファイル変更ジャーナル）と Git 作業ツリー差分を切り替えて表示する。
/// ロジックは <see cref="DiffSessionViewModel"/> に集約し、ここはダブルクリック等のビュー操作のみ。
/// </summary>
public partial class DiffSessionView : UserControl
{
    public DiffSessionView()
    {
        InitializeComponent();
    }

    private DiffSessionViewModel? Vm => DataContext as DiffSessionViewModel;

    /// <summary>ファイル行ダブルクリック：エディタで開く。</summary>
    private void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { SelectedFile: { } file } vm)
            vm.OpenInEditorCommand.Execute(file);
    }
}
