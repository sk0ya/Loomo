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

    /// <summary>入力欄・候補一覧からフォーカスが外れたら畳む。ただし住所欄の内側
    /// （入力欄⇔候補一覧）を行き来しているだけのときは畳まない（候補を選ぶ前に消えてしまう）。
    ///
    /// <para>候補一覧にも同じハンドラを付けてある——入力欄から候補へ降りたあとは入力欄の
    /// <c>LostKeyboardFocus</c> はもう鳴らないので、片方だけに付けると「候補へ降りたら最後、
    /// どこへフォーカスが移っても住所欄が開きっぱなし」になる。</para></summary>
    private void OnAddressLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject next && IsWithinAddressUi(next))
            return;
        DismissAddressEdit();
    }

    /// <summary>入力欄を畳んでパンくずへ戻す。
    ///
    /// <para>畳むと入力欄は <c>Collapsed</c> になるので、そこにキーボードフォーカスが残っていた場合は
    /// WPF がフォーカスをウィンドウの根へ落とす。根に落ちるとキー入力はどのペインにも届かず、
    /// Ctrl+L も一覧のカーソル移動も無反応になる（フォーカスを取れない余白を押して畳んだときが
    /// まさにこれ）。押した先が自分でフォーカスを取るならそちらが勝つので、<b>誰も取らなかったとき
    /// だけ</b>一覧へ返す——判定は押下の処理が終わったあとでないとできないので一度譲る。</para></summary>
    private void DismissAddressEdit()
    {
        if (Vm is not { IsAddressEditing: true })
            return;
        var hadKeyboardFocus = AddressBox.IsKeyboardFocusWithin;
        Vm.CancelAddressEdit();
        UpdateAddressSuggestionPopup();
        if (!hadKeyboardFocus)
            return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (Keyboard.FocusedElement is null or Window)
                EntryList.Focus();
        }));
    }

    /// <summary>住所欄そのもの（入力欄の枠）か、候補ポップアップの中か。</summary>
    private bool IsWithinAddressUi(DependencyObject node)
    {
        for (var current = node; current is not null; current = ParentOf(current))
            if (ReferenceEquals(current, AddressEditor) || ReferenceEquals(current, AddressSuggestionRoot))
                return true;
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject node)
        => node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
            ? System.Windows.Media.VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    // ===== 外側クリックで畳む =====
    // フォーカスが外れたら畳む、だけでは足りない。WPF はフォーカスを取れない要素
    // （ツールバーの余白・見出し・他ペインの地の部分）を押してもキーボードフォーカスを
    // 動かさないので、LostKeyboardFocus が鳴らず住所欄が開きっぱなしになる。入力中だけ
    // ウィンドウ全体の押下を見張り、住所欄の外を押されたら畳む。

    private Window? _addressDismissWindow;

    /// <summary>入力中だけウィンドウの押下を見張る。開閉のたびに呼ばれる
    /// （<see cref="UpdateAddressSuggestionPopup"/> 経由）ので、状態と見張りが必ず揃う。</summary>
    private void SyncAddressDismissWatch()
    {
        var window = Vm is { IsAddressEditing: true } ? OwnerWindow : null;
        if (ReferenceEquals(window, _addressDismissWindow))
            return;

        if (_addressDismissWindow is not null)
            _addressDismissWindow.RemoveHandler(PreviewMouseDownEvent,
                new MouseButtonEventHandler(OnWindowMouseDownWhileAddressEditing));
        _addressDismissWindow = window;
        // 行やボタンが押下を Handled にしていても畳みたいので handledEventsToo。
        _addressDismissWindow?.AddHandler(PreviewMouseDownEvent,
            new MouseButtonEventHandler(OnWindowMouseDownWhileAddressEditing), handledEventsToo: true);
    }

    private void OnWindowMouseDownWhileAddressEditing(object sender, MouseButtonEventArgs e)
    {
        // 押下は握り潰さない（畳んだうえで、押した先の操作はそのまま通す）。
        if (e.OriginalSource is DependencyObject source && IsWithinAddressUi(source))
            return;
        DismissAddressEdit();
    }

    /// <summary>候補ポップアップの開閉。入力中で、出すものがあるときだけ開く。</summary>
    private void UpdateAddressSuggestionPopup()
    {
        AddressSuggestionPopup.IsOpen = Vm is { IsAddressEditing: true }
            && (Vm.AddressSuggestions.Count > 0 || Vm.HasAddressError);
        SyncAddressDismissWatch();
    }
}
