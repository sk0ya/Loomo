using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// チルトホイール（横スクロール）対応。WPF は WM_MOUSEHWHEEL を標準では処理しないため、
/// ウィンドウの WndProc フックから呼び出し、カーソル直下の ScrollViewer を水平スクロールさせる。
/// FolderTree・タブストリップなど、横にあふれる ScrollViewer すべてに効く。
/// </summary>
internal static class HorizontalWheelScroll
{
    public const int WM_MOUSEHWHEEL = 0x020E;

    /// <summary>WM_MOUSEHWHEEL を処理する。スクロールできたら true（呼び元で handled にする）。</summary>
    public static bool Handle(IntPtr wParam)
    {
        // wParam の上位ワードが回転量（120/ノッチ、正＝右）。
        var delta = (short)(((long)wParam >> 16) & 0xFFFF);
        if (delta == 0)
            return false;

        var viewer = FindHorizontallyScrollable(Mouse.DirectlyOver as DependencyObject);
        if (viewer is null)
            return false;

        viewer.ScrollToHorizontalOffset(
            Math.Clamp(viewer.HorizontalOffset + delta, 0, viewer.ScrollableWidth));
        return true;
    }

    /// <summary>カーソル直下の要素から、横スクロール余地のある最も近い ScrollViewer を探す。</summary>
    private static ScrollViewer? FindHorizontallyScrollable(DependencyObject? source)
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (current is ScrollViewer { ScrollableWidth: > 0 } viewer)
                return viewer;
        }
        return null;
    }

    private static DependencyObject? GetParent(DependencyObject current)
        => current is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(current)
            : LogicalTreeHelper.GetParent(current);
}
