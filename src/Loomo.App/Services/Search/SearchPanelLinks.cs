using System.ComponentModel;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 検索ペインと部屋の他の面とのつなぎ（設計書 §23.3.1）。
/// <list type="bullet">
/// <item>タブに残した検索結果（正本は <see cref="SearchPanelViewModel.PinnedTabs"/>）を、エディタ／ブラウザ／
/// ターミナルと同じタブの仕組み（<see cref="TabsViewModel"/>＝サイドバーの TABS とペインヘッダーの ▾ 一覧）へ写す。
/// 検索ペインだけの別のタブ UI は作らない。</item>
/// <item>検索結果の「ペグボードへ」を <see cref="PegboardViewModel"/> のテキスト項目にする（§23.3 素材の流れ）。</item>
/// </list>
/// ShellWindow に置くと試せないので、VM 同士の配線だけをここへ切り出す（呼ぶのは <see cref="ShellViewModel"/>）。
/// </summary>
public static class SearchPanelLinks
{
    public static void Connect(SearchPanelViewModel search, TabsViewModel tabs, PegboardViewModel pegboard)
    {
        void Sync() => tabs.SyncSearchTabs(
            search.PinnedTabs.Select(t => (t.Id, t.Title, (string?)t.Summary)).ToList(),
            search.ActiveTab?.Id);

        search.PinnedTabs.CollectionChanged += (_, _) => Sync();
        search.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SearchPanelViewModel.ActiveTab))
                tabs.ActivateSearchTab(search.ActiveTab?.Id);
        };
        search.PegboardSendRequested += (_, payload) =>
            pegboard.AddContent(payload.Content, type: "text", title: payload.Title);
        Sync();
    }
}
