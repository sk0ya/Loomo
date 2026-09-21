using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>アドレス入力のコマンドを実行し、WPFのフォーカス操作は呼び出し側へ委譲する。</summary>
internal static class FilesColumnAddressController
{
    public static bool HandleEditorKeyDown(
        FilesColumnViewModel? vm,
        Key key,
        int suggestionCount,
        string addressText,
        Action focusList,
        Action moveToSuggestion,
        Action updatePopup)
    {
        if (vm is null)
            return false;

        switch (FilesColumnAddressPolicy.ResolveEditorKey(key, suggestionCount))
        {
            case FilesColumnAddressAction.Cancel:
                vm.CancelAddressEdit();
                updatePopup();
                focusList();
                return true;
            case FilesColumnAddressAction.Navigate:
                if (vm.NavigateAddress(addressText))
                    focusList();
                updatePopup();
                return true;
            case FilesColumnAddressAction.MoveToSuggestions:
                moveToSuggestion();
                return true;
            default:
                return false;
        }
    }

    public static bool HandleSuggestionKeyDown(
        FilesColumnViewModel? vm,
        Key key,
        int selectedIndex,
        string? selectedPath,
        Action focusList,
        Action focusEditor,
        Action updatePopup)
    {
        switch (FilesColumnAddressPolicy.ResolveSuggestionKey(key, selectedIndex))
        {
            case FilesColumnAddressAction.AcceptSuggestion:
                ApplySuggestion(vm, selectedPath, focusList, updatePopup);
                return true;
            case FilesColumnAddressAction.Cancel:
                vm?.CancelAddressEdit();
                updatePopup();
                focusList();
                return true;
            case FilesColumnAddressAction.ReturnToEditor:
                focusEditor();
                return true;
            default:
                return false;
        }
    }

    public static void ApplySuggestion(
        FilesColumnViewModel? vm,
        string? selectedPath,
        Action focusList,
        Action updatePopup)
    {
        if (vm is null || selectedPath is null)
            return;
        if (vm.NavigateAddress(selectedPath))
            focusList();
        updatePopup();
    }
}
