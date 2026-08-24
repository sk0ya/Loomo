using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>ファイル一覧ペインの編集可能なアドレス欄。
///
/// <para>エクスプローラーと同じく、住所は<b>ふだんパンくず・必要なとき入力欄</b>の一行で、
/// <c>Ctrl+L</c>（またはパンくずの余白クリック）で入力欄に変わり、Enter で移動、Esc で戻る。
/// アドレス欄はファイル一覧の道具であってサイドバーのツリーの道具ではない——ツリーの根を
/// 打ち替えるのではなく、「いま見ている場所」を打ち替えるためのもの。</para></summary>
public partial class FilesColumnView
{
    private void OnColumnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.L
            || (e.KeyboardDevice.Modifiers & ModifierKeys.Control) == 0
            || (e.KeyboardDevice.Modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
            return;

        BeginAddressEdit();
        e.Handled = true;
    }

    /// <summary>Ctrl+L：入力欄を開いて全選択する。ペインのどこにフォーカスがあっても効く。</summary>
    private void BeginAddressEdit()
    {
        if (Vm is null)
            return;
        Vm.BeginAddressEdit();
        // 出したばかりの入力欄はまだ配置されていないので、レイアウト後にフォーカスする。
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            AddressBox.Focus();
            AddressBox.SelectAll();
            UpdateAddressSuggestionPopup();
        }));
    }

    private void OnBreadcrumbBlankClick(object sender, MouseButtonEventArgs e)
    {
        // パンくずのボタン自身は自分でクリックを処理するので、ここへ来るのは余白だけ。
        BeginAddressEdit();
        e.Handled = true;
    }

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is null)
            return;

        switch (e.Key)
        {
            case Key.Escape:
                Vm.CancelAddressEdit();
                UpdateAddressSuggestionPopup();
                EntryList.Focus();
                e.Handled = true;
                break;

            case Key.Enter:
                if (Vm.NavigateAddress(AddressBox.Text))
                    EntryList.Focus();
                UpdateAddressSuggestionPopup();
                e.Handled = true;
                break;

            // 候補へ降りる。候補が無いときは入力欄に留まる（何も起きないより驚かない）。
            case Key.Down when AddressSuggestionList.Items.Count > 0:
                AddressSuggestionList.SelectedIndex = 0;
                (AddressSuggestionList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnAddressSuggestionKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                ApplySelectedSuggestion();
                e.Handled = true;
                break;

            case Key.Escape:
                Vm?.CancelAddressEdit();
                UpdateAddressSuggestionPopup();
                EntryList.Focus();
                e.Handled = true;
                break;

            // 先頭からさらに上へ行こうとしたら入力欄へ戻す（打ち直しに戻れる道を残す）。
            case Key.Up when AddressSuggestionList.SelectedIndex <= 0:
                AddressBox.Focus();
                AddressBox.SelectAll();
                e.Handled = true;
                break;
        }
    }

    private void OnAddressSuggestionClick(object sender, MouseButtonEventArgs e)
    {
        ApplySelectedSuggestion();
        e.Handled = true;
    }

    private void ApplySelectedSuggestion()
    {
        if (Vm is null || AddressSuggestionList.SelectedItem is not string path)
            return;
        if (Vm.NavigateAddress(path))
            EntryList.Focus();
        UpdateAddressSuggestionPopup();
    }

    /// <summary>入力欄からフォーカスが外れたら畳む。ただし候補一覧へ移ったときは畳まない
    /// （候補を選ぶ前に消えてしまう）。</summary>
    private void OnAddressLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject next && IsWithinAddressPopup(next))
            return;
        Vm?.CancelAddressEdit();
        UpdateAddressSuggestionPopup();
    }

    private bool IsWithinAddressPopup(DependencyObject node)
    {
        for (var current = node; current is not null; current = VisualTreeHelperParent(current))
            if (ReferenceEquals(current, AddressSuggestionList))
                return true;
        return false;
    }

    private static DependencyObject? VisualTreeHelperParent(DependencyObject node)
        => node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
            ? System.Windows.Media.VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    /// <summary>候補ポップアップの開閉。入力中で、出すものがあるときだけ開く。</summary>
    private void UpdateAddressSuggestionPopup()
    {
        AddressSuggestionPopup.IsOpen = Vm is { IsAddressEditing: true }
            && (Vm.AddressSuggestions.Count > 0 || Vm.HasAddressError);
    }
}
