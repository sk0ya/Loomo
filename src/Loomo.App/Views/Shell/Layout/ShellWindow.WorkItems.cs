using System.Windows.Controls.Primitives;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ActivityBar の Work Items（⌨ の上の 📋）。一覧はボタンの右へ、下端をボタンに揃えて開く
/// ——ボタンは窓の下の方にあるので、上端を揃えると一覧が窓の外へはみ出す。行を押すとブラウザペインで開く
/// （外のブラウザへ投げない：部屋の中で読む）。</summary>
public partial class ShellWindow {
    private void InitializeWorkItems() {
        if (_vm.WorkItems is not { } workItems)
            return;
        WorkItemsPopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [new CustomPopupPlacement(new Point(targetSize.Width, targetSize.Height - popupSize.Height),
                PopupPrimaryAxis.Horizontal)];
        workItems.OpenRequested += (url, title) => _ = OpenUrlInBrowserAsync(url, title);
    }
}
