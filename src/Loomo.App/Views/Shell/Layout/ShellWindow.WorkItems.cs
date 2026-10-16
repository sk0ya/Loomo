namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: サイドバーの Work Items（ActivityBar の 📋、既定は上段のソリューションの下）。
/// 面が見えたら古い一覧を取り直し、絞り込み欄へ入る。行を押すとブラウザペインで開く
/// （外のブラウザへ投げない：部屋の中で読む）。</summary>
public partial class ShellWindow {
    private void InitializeWorkItems() {
        if (_vm.WorkItems is not { } workItems)
            return;
        workItems.OpenRequested += (url, title) => _ = OpenUrlInBrowserAsync(url, title);
        SidebarWorkItems.IsVisibleChanged += (_, e) => {
            var shown = (bool)e.NewValue;
            workItems.IsShown = shown;
            // 開いたらすぐ打ち込めるように絞り込み欄へ（描画後でないとフォーカスが入らない）。
            // 起動時の復元で見えたときは取らない——人間が触る前にエディタのフォーカスを奪ってしまう。
            if (shown && IsLoaded)
                Dispatcher.BeginInvoke(SidebarWorkItems.FocusFilter,
                    System.Windows.Threading.DispatcherPriority.Input);
        };
    }
}
