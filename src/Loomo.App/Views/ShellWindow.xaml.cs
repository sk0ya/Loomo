using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Services;
using Editor.Controls;
using Terminal.Tabs;

namespace sk0ya.Loomo.App.Views;

public partial class ShellWindow : Window
{
    private readonly TerminalService _terminal;
    private readonly EditorService _editor;
    private readonly IWorkspaceService _workspace;
    private const string DefaultBrowserUrl = "https://www.google.com/";

    /// <summary>サイドバーを閉じる直前の幅を保持し、再表示時に復元する。</summary>
    private GridLength _savedSidebarWidth = new(220);

    public ShellWindow(
        ShellViewModel vm,
        TerminalService terminal,
        EditorService editor,
        IWorkspaceService workspace)
    {
        InitializeComponent();
        DataContext = vm;
        _terminal = terminal;
        _editor = editor;
        _workspace = workspace;

        // サイドバーの開閉に追従して列幅・スプリッターを切り替える
        vm.PropertyChanged += OnShellPropertyChanged;
        StateChanged += OnWindowStateChanged;
        Loaded += OnLoaded;

        var startDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // sk0ya コントロールを生成してホストへ配置し、サービスへ結びつける
        var termView = new TerminalTabView("powershell.exe", startDir);
        TerminalHost.Child = termView;
        _terminal.Attach(termView);
        _terminal.SetWorkingDirectory(startDir);

        var editorCtrl = new VimEditorControl();
        EditorHost.Child = editorCtrl;
        _editor.Attach(editorCtrl);

        // フォルダを開いたらエージェントの作業ディレクトリを同期
        _workspace.RootChanged += (_, root) =>
        {
            if (!string.IsNullOrEmpty(root)) _terminal.SetWorkingDirectory(root);
        };

        // ファイル選択でエディタに開く
        _workspace.SelectionChanged += async (_, path) =>
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                await _editor.OpenFileAsync(path);
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        BrowserAddressBox.Text = DefaultBrowserUrl;

        try
        {
            await BrowserView.EnsureCoreWebView2Async();
            BrowserView.Source = new Uri(DefaultBrowserUrl);
        }
        catch (Exception ex)
        {
            BrowserAddressBox.Text = $"WebView2 initialization failed: {ex.Message}";
        }
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsSidebarVisible) && sender is ShellViewModel vm)
            ApplySidebarVisibility(vm.IsSidebarVisible);
    }

    private void ApplySidebarVisibility(bool visible)
    {
        if (visible)
        {
            SidebarColumn.MinWidth = 120;
            SidebarColumn.Width = _savedSidebarWidth.Value > 0 ? _savedSidebarWidth : new GridLength(220);
            SidebarSplitterColumn.Width = new GridLength(4);
            SidebarContainer.Visibility = Visibility.Visible;
            SidebarSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            _savedSidebarWidth = SidebarColumn.Width;
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(0);
            SidebarSplitterColumn.Width = new GridLength(0);
            SidebarContainer.Visibility = Visibility.Collapsed;
            SidebarSplitter.Visibility = Visibility.Collapsed;
        }
    }

    // ===== カスタムタイトルバー（WindowChrome） =====

    // 単一の四角（最大化）/ 二重の四角（元に戻す）をベクターで描く。
    private static readonly Geometry MaximizeGeometry = Geometry.Parse("M0.5,0.5 H9.5 V9.5 H0.5 Z");
    private static readonly Geometry RestoreGeometry = Geometry.Parse("M2.5,2.5 V0.5 H9.5 V7.5 H7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z");

    private void OnMinimize(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnBrowserBack(object sender, RoutedEventArgs e)
    {
        if (BrowserView.CanGoBack)
            BrowserView.GoBack();
    }

    private void OnBrowserForward(object sender, RoutedEventArgs e)
    {
        if (BrowserView.CanGoForward)
            BrowserView.GoForward();
    }

    private void OnBrowserReload(object sender, RoutedEventArgs e)
        => BrowserView.CoreWebView2?.Reload();

    private void OnBrowserGo(object sender, RoutedEventArgs e)
        => NavigateBrowser(BrowserAddressBox.Text);

    private void OnBrowserAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateBrowser(BrowserAddressBox.Text);
            e.Handled = true;
        }
    }

    private void OnBrowserNavigationCompleted(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        if (BrowserView.Source is not null)
            BrowserAddressBox.Text = BrowserView.Source.ToString();
    }

    private void NavigateBrowser(string text)
    {
        var address = NormalizeBrowserAddress(text);
        BrowserAddressBox.Text = address;

        if (BrowserView.CoreWebView2 is not null)
            BrowserView.CoreWebView2.Navigate(address);
    }

    private static string NormalizeBrowserAddress(string text)
    {
        var address = text.Trim();
        if (string.IsNullOrWhiteSpace(address))
            return DefaultBrowserUrl;

        if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Scheme))
            return uri.ToString();

        if (address.Contains(' '))
            return $"https://www.google.com/search?q={Uri.EscapeDataString(address)}";

        var scheme = address.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
                     || address.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            ? "http://"
            : "https://";

        return scheme + address;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        // 最大化/復元アイコンを切り替える。
        MaximizeIcon.Data = maximized ? RestoreGeometry : MaximizeGeometry;
        MaximizeButton.ToolTip = maximized ? "元に戻す" : "最大化";
    }

    // ===== 最大化時にタスクバーを覆わないようワーク領域へ制限する（WindowStyle=None 対策） =====
    //
    // WindowStyle="None" のボーダレスウィンドウは、最大化するとモニタ全体（タスクバー含む）に
    // 広がってしまい、最下部の AI バーがタスクバーの裏に隠れる。WM_GETMINMAXINFO を処理して
    // 最大化サイズをモニタのワーク領域（タスクバーを除いた範囲）に収める。

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        source.AddHook(WndProc);
    }

    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref monitorInfo))
                {
                    var work = monitorInfo.rcWork;
                    var mon = monitorInfo.rcMonitor;
                    var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                    // 最大化位置とサイズをワーク領域基準（モニタ左上からの相対）に設定する。
                    mmi.ptMaxPosition.X = work.Left - mon.Left;
                    mmi.ptMaxPosition.Y = work.Top - mon.Top;
                    mmi.ptMaxSize.X = work.Right - work.Left;
                    mmi.ptMaxSize.Y = work.Bottom - work.Top;
                    Marshal.StructureToPtr(mmi, lParam, true);
                    handled = true;
                }
            }
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }
}
