using System;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>ペグボードペイン（設計書 §23.3）。
/// ロジックは <see cref="ViewModels.PegboardViewModel"/> に集約する。
/// カードはテキスト（file はファイル参照も）としてドラッグでき、
/// エディタ・ターミナル・外部アプリへ落とせる（素材の流れ）。</summary>
public partial class PegboardView : UserControl
{
    private Point _dragStart;
    private bool _dragArmed;

    public PegboardView()
    {
        InitializeComponent();
    }

    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
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
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
    }
}
