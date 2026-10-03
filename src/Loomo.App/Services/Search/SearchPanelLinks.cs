using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 検索ペインと部屋の他の面とのつなぎ（設計書 §23.3.1）。検索結果の「ペグボードへ」を
/// <see cref="PegboardViewModel"/> のテキスト項目にする（§23.3 素材の流れ）。
/// 残した検索結果のタブは検索ペインの中の道具なので、サイドバーの TABS には写さない。
/// ShellWindow に置くと試せないので、VM 同士の配線だけをここへ切り出す（呼ぶのは <see cref="ShellViewModel"/>）。
/// </summary>
public static class SearchPanelLinks
{
    public static void Connect(SearchPanelViewModel search, PegboardViewModel pegboard)
        => search.PegboardSendRequested += (_, payload) =>
            pegboard.AddContent(payload.Content, type: "text", title: payload.Title);
}
