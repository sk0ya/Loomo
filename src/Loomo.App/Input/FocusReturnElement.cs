using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace sk0ya.Loomo.App.Input;

/// <summary>覚えておいたフォーカス要素が「今も戻せる状態か」を判定する（<see cref="FocusReturnPolicy"/> の
/// <c>elementAlive</c> を作る側）。要素は弱参照で持つので、タブやペインごと捨てられていれば単に取れなくなる。
/// 取れても、非表示・無効・別ウィンドウ（切り離し後）なら戻り先にしない。</summary>
public static class FocusReturnElement
{
    /// <summary><paramref name="element"/> が <paramref name="ancestor"/> の配下（自分自身を含む）か。
    /// ビジュアルツリーと論理ツリーを跨いで遡る（ポップアップやコンテンツホストの境界で切れないように）。</summary>
    public static bool IsWithin(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = AnyParent(current))
            if (ReferenceEquals(current, ancestor))
                return true;
        return false;
    }

    /// <summary>覚えておいた要素が今もフォーカスを受け取れる状態で <paramref name="owner"/> の中にあれば返す。</summary>
    public static IInputElement? ResolveLive(WeakReference<IInputElement>? reference, DependencyObject owner)
    {
        if (reference?.TryGetTarget(out var candidate) != true)
            return null;
        if (candidate is not DependencyObject element || candidate is not UIElement { IsVisible: true, IsEnabled: true })
            return null;
        return IsWithin(element, owner) ? candidate : null;
    }

    private static DependencyObject? AnyParent(DependencyObject d)
        => d is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(d)
            : LogicalTreeHelper.GetParent(d);
}
