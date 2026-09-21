using System;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>アドレス入力、候補一覧、候補の選択操作をまとめて扱う。</summary>
internal sealed class BrowserAddressSuggestionController
{
    private readonly BrowserViewModel _viewModel;
    private readonly TextBox _addressBox;
    private readonly ListBox _suggestions;
    private readonly Func<string?> _currentAddress;
    private readonly Action<string> _navigate;
    private bool _suppressSuggestions;

    internal BrowserAddressSuggestionController(
        BrowserViewModel viewModel,
        TextBox addressBox,
        ListBox suggestions,
        Func<string?> currentAddress,
        Action<string> navigate)
    {
        _viewModel = viewModel;
        _addressBox = addressBox;
        _suggestions = suggestions;
        _currentAddress = currentAddress;
        _navigate = navigate;
    }

    internal void SetText(string text)
    {
        _suppressSuggestions = true;
        try { _addressBox.Text = text; }
        finally { _suppressSuggestions = false; }
        CloseSuggestions();
    }

    internal void OnTextChanged()
    {
        if (!_suppressSuggestions)
            _viewModel.UpdateSuggestions(_addressBox.Text);
    }

    internal void OnLostFocus()
    {
        if (!_suggestions.IsKeyboardFocusWithin && !_suggestions.IsMouseOver)
            CloseSuggestions();
    }

    internal void CloseSuggestions()
    {
        _viewModel.IsSuggestionsOpen = false;
        _suggestions.SelectedIndex = -1;
    }

    internal void HandleKey(KeyEventArgs e)
    {
        switch (BrowserChromePolicy.ResolveAddressAction(e.Key, _viewModel.IsSuggestionsOpen))
        {
            case BrowserAddressKeyAction.Commit:
                var address = BrowserChromePolicy.AddressToCommit(
                    _viewModel.IsSuggestionsOpen,
                    (_suggestions.SelectedItem as BrowserLinkViewModel)?.Url,
                    _addressBox.Text);
                CloseSuggestions();
                _navigate(address);
                break;
            case BrowserAddressKeyAction.MoveDown:
                MoveSuggestion(1);
                break;
            case BrowserAddressKeyAction.MoveUp:
                MoveSuggestion(-1);
                break;
            case BrowserAddressKeyAction.DismissSuggestions:
                CloseSuggestions();
                break;
            case BrowserAddressKeyAction.RestoreCurrentAddress:
                SetText(_currentAddress() ?? string.Empty);
                break;
            case null:
                return;
        }
        e.Handled = true;
    }

    private void MoveSuggestion(int delta)
    {
        var next = BrowserChromePolicy.NextSuggestionIndex(
            _suggestions.SelectedIndex, _suggestions.Items.Count, delta);
        if (next < 0) return;
        _suggestions.SelectedIndex = next;
        _suggestions.ScrollIntoView(_suggestions.SelectedItem);
    }
}
