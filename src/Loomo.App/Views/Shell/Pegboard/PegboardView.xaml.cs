using System;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>ペグボードペイン（設計書 §23.3）。
/// ロジックは <see cref="ViewModels.PegboardViewModel"/> に集約する。
/// カードはテキスト（file はファイル参照も）としてドラッグでき、
/// エディタ・ターミナル・外部アプリへ落とせる（素材の流れ）。逆にボードへ落とせば残せる。</summary>
public partial class PegboardView : UserControl
{
    private Point _dragStart;
    private bool _dragArmed;
    /// <summary>自分のカードをドラッグ中（自分自身へのドロップで複製しないため）。</summary>
    private bool _draggingOwnCard;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMinutes(1) };

    public PegboardView()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => Vm?.RefreshTimes();
    }

    private PegboardViewModel? Vm => DataContext as PegboardViewModel;

    // 「3分前」の表記は表示中だけ1分ごとに引き直す。
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Vm?.RefreshTimes();
        _clock.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _clock.Stop();

    private void OnPanelKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (ctrl && e.Key == Key.F)
        {
            ShowFilter(vm);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && vm.IsFilterVisible)
        {
            vm.CloseFilter();
            Focus();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.V && e.OriginalSource is not TextBox)
        {
            vm.AddFromClipboardCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnToggleFilter(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (vm.IsFilterVisible) vm.CloseFilter();
        else ShowFilter(vm);
    }

    private void OnCloseFilter(object sender, RoutedEventArgs e) => Vm?.CloseFilter();

    private void ShowFilter(PegboardViewModel vm)
    {
        vm.IsFilterVisible = true;
        Dispatcher.BeginInvoke(() =>
        {
            FilterBox.Focus();
            FilterBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (AddButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = AddButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        // カード上の操作ボタンはそれぞれのコマンドに任せる（ドラッグ・ダブルクリックの対象外）。
        if (IsInsideButton(e.OriginalSource as DependencyObject, (DependencyObject)sender))
        {
            _dragArmed = false;
            return;
        }
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: PegboardItemVm item })
        {
            _dragArmed = false;
            Vm?.OpenCommand.Execute(item);
            e.Handled = true;
            return;
        }
        _dragStart = e.GetPosition(this);
        _dragArmed = true;
    }

    private void OnCardMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed
            || sender is not FrameworkElement { DataContext: PegboardItemVm item })
            return;

        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragArmed = false;
        var data = new DataObject();
        data.SetText(item.Content);
        if (item.Type == "file" && (File.Exists(item.Content) || Directory.Exists(item.Content)))
            data.SetFileDropList(new StringCollection { item.Content });
        _draggingOwnCard = true;
        try { DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy); }
        finally { _draggingOwnCard = false; }
    }

    private void OnBoardDragOver(object sender, DragEventArgs e)
    {
        var accepts = !_draggingOwnCard
            && (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText));
        e.Effects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>ボードへのドロップ＝「残す」。ファイルはパスごとに1枚、テキストは種別を自動判別して1枚。</summary>
    private void OnBoardDrop(object sender, DragEventArgs e)
    {
        if (_draggingOwnCard || Vm is not { } vm) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            foreach (var path in paths)
                vm.AddContent(path, type: "file");
        }
        else if (e.Data.GetData(DataFormats.UnicodeText) is string text)
        {
            vm.AddContent(text);
        }
        e.Handled = true;
    }

    private static bool IsInsideButton(DependencyObject? node, DependencyObject stop)
    {
        for (; node is not null && node != stop; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is ButtonBase) return true;
        }
        return false;
    }
}
