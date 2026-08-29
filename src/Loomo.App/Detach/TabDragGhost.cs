using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Detach;

/// <summary>
/// タブを窓の<b>外</b>へ運んでいるあいだ、カーソルに付いてくる半透明のタブ片。
///
/// <para>帯の中の並べ替えは自前で描いている（<c>DetachedPaneWindow.TabDrag.cs</c>）が、帯から抜けた先は
/// OLE の <see cref="DragDrop.DoDragDrop"/> ＝ OS のカーソル（移動／禁止）しか出ない。掴んだ物が手元から
/// 消えてしまうので、運んでいるタブそのものを小さな窓で描いて追従させ、<b>離したら何が起きるか</b>
/// （他の窓の帯へ結合／新しい窓へ分離）を一行で添える。</para>
///
/// <para>この窓はドロップ先の当たり判定を邪魔してはいけない——カーソルの真下に居るので、素のままだと
/// <c>WindowFromPoint</c> がこの窓を返してドロップ先の帯へ届かなくなる。<c>WS_EX_TRANSPARENT</c> で
/// 素通しにし、<c>WS_EX_NOACTIVATE</c>＋<see cref="Window.ShowActivated"/>=false でドラッグ中の
/// アクティブも奪わない。</para>
/// </summary>
internal sealed class TabDragGhost : IDisposable
{
    /// <summary>運んでいるあいだ、元の場所に残すタブの薄さ（掴んだ物がどこから出たか見失わないため）。</summary>
    public const double TornSourceOpacity = 0.35;

    /// <summary>カーソルからずらす量（矢印の下に敷いて、掴んで運んでいる見え方にする）。</summary>
    private const double CursorOffsetX = 14;
    private const double CursorOffsetY = 18;

    private readonly Window _window;
    private readonly TextBlock _hint;
    private readonly DpiScale _dpi;
    private readonly DispatcherTimer _follow;
    private DragDropEffects? _shownFor;
    private bool _closed;

    /// <summary>窓の外で離したら本当に新しい窓へ分かれるか（元の窓に1枚しか無ければ何も起きない）。</summary>
    private bool _canSplit = true;

    private TabDragGhost(Window owner, string title, ImageSource? icon)
    {
        _dpi = VisualTreeHelper.GetDpi(owner);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon is not null)
            row.Children.Add(new Image
            {
                Source = icon, Width = 14, Height = 14,
                Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            });
        row.Children.Add(new TextBlock
        {
            Text = title, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = UiFontManager.Scaled(12.5), Foreground = Resource("Fg", Brushes.Black),
        });

        _hint = new TextBlock
        {
            Margin = new Thickness(0, 3, 0, 0),
            FontSize = UiFontManager.Scaled(10), Foreground = Resource("FgDim", Brushes.Gray),
        };

        var body = new StackPanel();
        body.Children.Add(row);
        body.Children.Add(_hint);

        var chip = new Border
        {
            Background = Resource("Panel", Brushes.White),
            BorderBrush = Resource("Accent", Brushes.DodgerBlue),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(9, 6, 9, 6),
            Margin = new Thickness(10),   // 影のぶんの余白（透明ウィンドウなので中で確保する）
            Child = body,
            Effect = new DropShadowEffect
            {
                BlurRadius = 12, ShadowDepth = 2, Direction = 270, Opacity = 0.35,
                Color = Colors.Black,
            },
        };

        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            IsHitTestVisible = false,
            Focusable = false,
            Opacity = 0.92,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000,   // 位置が決まる前の1フレームを画面外で捨てる
            Content = chip,
        };
        _window.SourceInitialized += (_, _) => MakeClickThrough();

        // 位置はタイマーで追う。<see cref="DragDrop.GiveFeedback"/> はドロップ効果が変わったときにしか
        // 上がらず、同じ場所を素通りしているあいだ（＝帯へ戻ってくる途中）タブ片が置き去りになる
        // ——実機のドラッグで踏んだ。GiveFeedback は案内文（効果）の更新だけに使う。
        _follow = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16), DispatcherPriority.Normal,
            (_, _) => MoveToCursor(), Dispatcher.CurrentDispatcher);
    }

    /// <summary>タブ片を出してカーソルの下へ置く（<paramref name="owner"/> は DPI の基準に使う）。
    /// <paramref name="canSplit"/>＝窓の外で離したときに新しい窓へ分かれるか。1枚しか無い切り離し窓から
    /// 引き出しても何も起きない（<c>DetachedWindowManager.EndDrag</c> が弾く）ので、案内をそれに合わせる。</summary>
    public static TabDragGhost Show(Window owner, string title, ImageSource? icon, bool canSplit = true)
    {
        var ghost = new TabDragGhost(owner, title, icon) { _canSplit = canSplit };
        ghost._window.Show();
        ghost.Follow(DragDropEffects.None);
        return ghost;
    }

    /// <summary>いま離したら何が起きるかを書き替える（<see cref="GiveFeedbackEventArgs.Effects"/> を渡す）。</summary>
    public void Follow(DragDropEffects effects)
    {
        if (_closed)
            return;
        if (_shownFor != effects)
        {
            _shownFor = effects;
            _hint.Text = HintFor(effects, _canSplit);
        }
        MoveToCursor();
    }

    private void MoveToCursor()
    {
        if (_closed || !GetCursorPos(out var p))
            return;
        _window.Left = p.X / _dpi.DpiScaleX + CursorOffsetX;
        _window.Top = p.Y / _dpi.DpiScaleY + CursorOffsetY;
    }

    /// <summary>ドロップ先が受け取る構えなら「結合」、どこも受けないなら離した先が新しい窓になる。
    /// ただし分かれようのないタブ（1枚だけの切り離し窓）では、起きないことを約束しない。</summary>
    internal static string HintFor(DragDropEffects effects, bool canSplit = true)
        => effects != DragDropEffects.None ? "このタブ帯へ入れる"
            : canSplit ? "離すと新しいウィンドウ" : "すでに単独のウィンドウ";

    public void Dispose()
    {
        if (_closed)
            return;
        _closed = true;
        _follow.Stop();
        _window.Close();
    }

    private static Brush Resource(string key, Brush fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? fallback;

    /// <summary>カーソル直下に居ても当たり判定を素通しさせる（ドロップ先の帯へ届かせるため）。</summary>
    private void MakeClickThrough()
    {
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static int GetWindowLong(IntPtr hWnd, int index) => (int)GetWindowLongPtr(hWnd, index);

    private static void SetWindowLong(IntPtr hWnd, int index, int value)
        => SetWindowLongPtr(hWnd, index, new IntPtr(value));
}
