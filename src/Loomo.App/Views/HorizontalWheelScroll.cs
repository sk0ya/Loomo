using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Editor.Controls;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// チルトホイール（横スクロール）対応。WPF は WM_MOUSEHWHEEL を標準では処理しないため、
/// ウィンドウの WndProc フックから呼び出して水平スクロールを行う。
/// エディタ（VimEditorControl）は独自キャンバス描画で標準 ScrollViewer を持たないため、
/// 公開 API の ScrollHorizontalByWheelDelta を直接呼ぶ。それ以外（FolderTree・タブストリップなど
/// 横にあふれる ScrollViewer）はカーソル直下の ScrollViewer を辿ってスクロールさせる。
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

        var source = Mouse.DirectlyOver as DependencyObject;

        // エディタは独自キャンバスで描画され ScrollViewer を持たないので専用 API へ委譲する。
        var editor = FindAncestor<VimEditorControl>(source);
        if (editor is not null && editor.ScrollHorizontalByWheelDelta(delta))
            return true;

        var viewer = FindHorizontallyScrollable(source);
        if (viewer is null)
            return false;

        viewer.ScrollToHorizontalOffset(
            Math.Clamp(viewer.HorizontalOffset + delta, 0, viewer.ScrollableWidth));
        return true;
    }

    /// <summary>カーソル直下の要素から、指定型の最も近い祖先を探す。</summary>
    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (current is T match)
                return match;
        }
        return null;
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
