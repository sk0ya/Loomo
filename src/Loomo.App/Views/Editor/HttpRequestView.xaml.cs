using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>.http パネル（§24.30）。中身は <see cref="HttpRequestPanelViewModel"/>。</summary>
public partial class HttpRequestView : UserControl
{
    public HttpRequestView() => InitializeComponent();

    // 一覧で選んだ行を Enter で送る（マウスの ▶ だけにしない）。
    private void OnRequestListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not HttpRequestPanelViewModel vm)
            return;
        e.Handled = true;
        if (vm.SendSelectedCommand.CanExecute(null))
            vm.SendSelectedCommand.Execute(null);
    }
}
