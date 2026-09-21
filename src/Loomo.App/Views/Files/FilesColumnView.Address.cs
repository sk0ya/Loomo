using System.Windows.Input;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>ファイル一覧ペインの編集可能なアドレス欄。</summary>
public partial class FilesColumnView
{
    private FilesColumnAddressInteractionController? _addressInteraction;

    private FilesColumnAddressInteractionController AddressInteraction
        => _addressInteraction ??= new FilesColumnAddressInteractionController(
            EntryList,
            AddressBox,
            AddressSuggestionList,
            AddressSuggestionPopup,
            AddressEditor,
            AddressSuggestionRoot,
            () => Vm);

    private void OnColumnPreviewKeyDown(object sender, KeyEventArgs e)
        => AddressInteraction.OnColumnPreviewKeyDown(e);

    private void OnBreadcrumbBlankClick(object sender, MouseButtonEventArgs e)
        => AddressInteraction.OnBreadcrumbBlankClick(e);

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
        => AddressInteraction.OnAddressKeyDown(e);

    private void OnAddressSuggestionKeyDown(object sender, KeyEventArgs e)
        => AddressInteraction.OnSuggestionKeyDown(e);

    private void OnAddressSuggestionClick(object sender, MouseButtonEventArgs e)
        => AddressInteraction.OnSuggestionClick(e);

    private void OnAddressLostFocus(object sender, KeyboardFocusChangedEventArgs e)
        => AddressInteraction.OnLostFocus(e);

    // ViewModel更新後と Unloaded の住所欄キャンセルから呼ばれる共有更新口。
    private void UpdateAddressSuggestionPopup()
        => AddressInteraction.UpdateSuggestionPopup();
}
