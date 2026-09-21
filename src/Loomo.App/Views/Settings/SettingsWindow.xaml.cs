using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using sk0ya.Loomo.App.Services.Infrastructure;

namespace sk0ya.Loomo.App.Views;

/// <summary>設定の独立ウィンドウ。中身は <see cref="SettingsView"/>（DataContext は本体と同じ ShellViewModel）。
/// 中央オーバーレイをやめてウィンドウにしたので、移動・リサイズは OS のウィンドウ操作で行える。
/// 本体を Owner にするため常に手前に出て、本体終了時に一緒に閉じる。位置・サイズは永続化しない。</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => UpdateMaxRestoreGlyph();
    }

    /// <summary>最大化ボタンのアイコンとツールチップを状態に合わせる（本体・切り離しウィンドウと同じ見せ方）。</summary>
    private void UpdateMaxRestoreGlyph()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaxRestoreIcon.Data = maximized ? CaptionGlyphs.Restore : CaptionGlyphs.Maximize;
        MaxRestoreButton.ToolTip = maximized ? "元に戻す" : "最大化";
    }

    // ===== キャプション（WindowChrome：自前キャプション＋リサイズ枠だけ WindowChrome に任せる） =====

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaxRestore(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    /// <summary>キャプションの空き領域ドラッグでウィンドウ移動（ダブルクリックで最大化トグル）。
    /// 最大化中のドラッグは掴んだ位置への復元処理が要るので、ここでは受けない（ダブルクリックで戻せる）。</summary>
    private void OnCaptionMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (WpfTreeTraversal.FindVisualAncestor<ButtonBase>(
                e.OriginalSource as DependencyObject) is not null)
            return;
        if (e.ClickCount == 2)
        {
            OnMaxRestore(sender, e);
            return;
        }
        if (WindowState == WindowState.Maximized)
            return;

        ReleaseCapture();
        SendMessage(new WindowInteropHelper(this).Handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
    }

    /// <summary>Esc で閉じる。キーキャプチャ中（KeyCaptureBox にフォーカス）は取消に使うので横取りしない。</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || WpfTreeTraversal.FindVisualAncestor<KeyCaptureBox>(
                e.OriginalSource as DependencyObject) is not null)
            return;

        Close();
        e.Handled = true;
    }

    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private static readonly IntPtr HTCAPTION = 2;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
