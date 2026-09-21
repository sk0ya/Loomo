using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using sk0ya.Loomo.App.Services.Infrastructure;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>アドレス入力と候補ポップアップのキーボード・フォーカス・dismissライフサイクルを管理する。</summary>
internal sealed class FilesColumnAddressInteractionController
{
    private readonly ListBox _entryList;
    private readonly TextBox _addressBox;
    private readonly ListBox _suggestionList;
    private readonly Popup _suggestionPopup;
    private readonly FrameworkElement _addressEditor;
    private readonly FrameworkElement _suggestionRoot;
    private readonly Func<FilesColumnViewModel?> _getViewModel;
    private Window? _dismissWindow;

    internal FilesColumnAddressInteractionController(
        ListBox entryList,
        TextBox addressBox,
        ListBox suggestionList,
        Popup suggestionPopup,
        FrameworkElement addressEditor,
        FrameworkElement suggestionRoot,
        Func<FilesColumnViewModel?> getViewModel)
    {
        _entryList = entryList;
        _addressBox = addressBox;
        _suggestionList = suggestionList;
        _suggestionPopup = suggestionPopup;
        _addressEditor = addressEditor;
        _suggestionRoot = suggestionRoot;
        _getViewModel = getViewModel;
    }

    internal void OnColumnPreviewKeyDown(KeyEventArgs e)
    {
        if (!FilesColumnAddressPolicy.IsEditShortcut(e.Key, e.KeyboardDevice.Modifiers))
            return;

        BeginAddressEdit();
        e.Handled = true;
    }

    internal void OnBreadcrumbBlankClick(MouseButtonEventArgs e)
    {
        // パンくずのボタン自身は自分でクリックを処理するので、ここへ来るのは余白だけ。
        BeginAddressEdit();
        e.Handled = true;
    }

    internal void OnAddressKeyDown(KeyEventArgs e)
    {
        if (FilesColumnAddressController.HandleEditorKeyDown(
                _getViewModel(), e.Key, _suggestionList.Items.Count, _addressBox.Text,
                FocusList, FocusFirstSuggestion, UpdateSuggestionPopup))
            e.Handled = true;
    }

    internal void OnSuggestionKeyDown(KeyEventArgs e)
    {
        if (FilesColumnAddressController.HandleSuggestionKeyDown(
                _getViewModel(), e.Key, _suggestionList.SelectedIndex,
                _suggestionList.SelectedItem as string,
                FocusList, FocusAddressEditor, UpdateSuggestionPopup))
            e.Handled = true;
    }

    internal void OnSuggestionClick(MouseButtonEventArgs e)
    {
        FilesColumnAddressController.ApplySuggestion(
            _getViewModel(), _suggestionList.SelectedItem as string,
            FocusList, UpdateSuggestionPopup);
        e.Handled = true;
    }

    internal void OnLostFocus(KeyboardFocusChangedEventArgs e)
    {
        var focusWithin = e.NewFocus is DependencyObject next && IsWithinAddressUi(next);
        if (FilesColumnAddressPolicy.ShouldDismiss(_getViewModel()?.IsAddressEditing == true, focusWithin))
            DismissAddressEdit();
    }

    internal void UpdateSuggestionPopup()
    {
        var vm = _getViewModel();
        _suggestionPopup.IsOpen = FilesColumnAddressPolicy.ShouldShowPopup(
            vm?.IsAddressEditing == true, vm?.AddressSuggestions.Count ?? 0, vm?.HasAddressError == true);
        SyncDismissWatch();
    }

    private void BeginAddressEdit()
    {
        var vm = _getViewModel();
        if (vm is null)
            return;
        vm.BeginAddressEdit();
        // 出したばかりの入力欄はまだ配置されていないので、レイアウト後にフォーカスする。
        _addressBox.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            FocusAddressEditor();
            UpdateSuggestionPopup();
        }));
    }

    private void FocusFirstSuggestion()
    {
        _suggestionList.SelectedIndex = 0;
        (_suggestionList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
    }

    private void FocusAddressEditor()
    {
        _addressBox.Focus();
        _addressBox.SelectAll();
    }

    private void FocusList() => _entryList.Focus();

    private void DismissAddressEdit()
    {
        if (!FilesColumnAddressPolicy.ShouldDismiss(_getViewModel()?.IsAddressEditing == true,
                focusWithinAddressUi: false)
            || _getViewModel() is not { } vm)
            return;

        var hadKeyboardFocus = _addressBox.IsKeyboardFocusWithin;
        vm.CancelAddressEdit();
        UpdateSuggestionPopup();
        if (!FilesColumnAddressPolicy.ShouldRestoreListFocus(hadKeyboardFocus, focusIsUnclaimed: true))
            return;

        _addressBox.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (FilesColumnAddressPolicy.ShouldRestoreListFocus(
                    hadKeyboardFocus, Keyboard.FocusedElement is null or Window))
                _entryList.Focus();
        }));
    }

    private bool IsWithinAddressUi(DependencyObject node)
        => WpfTreeTraversal.HasAncestor(node, _addressEditor)
            || WpfTreeTraversal.HasAncestor(node, _suggestionRoot);

    private void SyncDismissWatch()
    {
        var window = _getViewModel() is { IsAddressEditing: true }
            ? Window.GetWindow(_addressBox)
            : null;
        if (ReferenceEquals(window, _dismissWindow))
            return;

        if (_dismissWindow is not null)
            _dismissWindow.RemoveHandler(UIElement.PreviewMouseDownEvent,
                new MouseButtonEventHandler(OnWindowMouseDownWhileAddressEditing));
        _dismissWindow = window;
        // 押下先が処理済みでも住所欄を畳む。
        _dismissWindow?.AddHandler(UIElement.PreviewMouseDownEvent,
            new MouseButtonEventHandler(OnWindowMouseDownWhileAddressEditing), handledEventsToo: true);
    }

    private void OnWindowMouseDownWhileAddressEditing(object sender, MouseButtonEventArgs e)
    {
        var focusWithin = e.OriginalSource is DependencyObject source && IsWithinAddressUi(source);
        if (FilesColumnAddressPolicy.ShouldDismiss(_getViewModel()?.IsAddressEditing == true, focusWithin))
            DismissAddressEdit();
    }
}
