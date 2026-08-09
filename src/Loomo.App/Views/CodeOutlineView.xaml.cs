using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// LSP のコード構造アウトライン＋②呼び出し解析を表示するネイティブ WPF ビュー（EditorSupport ペインの
/// コードフォールバック）。以前は WebView2 に HTML を描いていたが、初回コールドスタート・白フラッシュ・
/// HTML 生成コストを避けるため WPF へ移行（2026-07）。LSP 駆動は <see cref="ShellWindow"/> が担い、この
/// ビューは <see cref="ShowOutline"/>／<see cref="SetCurrentAndPanels"/>／<see cref="ShowNotice"/> で
/// モデルを受け取って描くだけ。ジャンプ・インストール等の操作は CLR イベントでホストへ返す。
/// </summary>
public partial class CodeOutlineView : UserControl
{
    private readonly CodeOutlineViewModel _vm = new();

    public CodeOutlineView()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    /// <summary>アウトラインのメンバー名／↦ クリック：ソース上のシンボル名へジャンプ。</summary>
    public event EventHandler<SourceLocationActivatedEventArgs>? SourceLocationActivated;

    /// <summary>②パネルの行クリック：別ファイル（または同一ファイル）の該当行を開く（1 始まり）。</summary>
    public event EventHandler<FileLocationActivatedEventArgs>? FileLocationActivated;

    /// <summary>案内ページの「インストール」。</summary>
    public event EventHandler? InstallRequested;

    /// <summary>案内ページの「LSP 設定を開く」。</summary>
    public event EventHandler? OpenLspSettingsRequested;

    /// <summary>案内ページの「導入手順を開く」（URL 付き）。</summary>
    public event EventHandler<string>? OpenDocsRequested;

    // 以下 3 つは内部モデル（CallPanels / LspNoticeModel.Notice は internal）を受けるため internal
    // （呼び出しは同一アセンブリの ShellWindow のみ）。

    /// <summary>アウトライン＋②パネルを（作り直して）表示する。<paramref name="currentLine1"/> は 0 で current 無し。</summary>
    internal void ShowOutline(IReadOnlyList<OutlineNode> roots, int currentLine1, CallPanels panels)
        => _vm.ShowOutline(roots, currentLine1, panels);

    /// <summary>キャレット追従：ツリーは作り直さず current 付替え＋②差し替えのみ（折りたたみを保つ）。</summary>
    internal void SetCurrentAndPanels(int currentLine1, CallPanels panels)
        => _vm.SetCurrentAndPanels(currentLine1, panels);

    /// <summary>キャレット移動時に current ハイライトだけを即時更新する。</summary>
    internal void SetCurrent(int currentLine1)
        => _vm.SetCurrent(currentLine1);

    /// <summary>LSP 解析完了後に②パネルだけを差し替える。</summary>
    internal void SetPanels(CallPanels panels)
        => _vm.SetPanels(panels);

    /// <summary>言語サーバー未接続／未導入の案内を表示する。</summary>
    internal void ShowNotice(LspNoticeModel.Notice notice)
        => _vm.ShowNotice(notice);

    /// <summary>
    /// TreeView 内部の ScrollViewer（スクロールバー Disabled でもホイールは握って Handled にする）が
    /// 外側 ScrollViewer へホイールを渡さず、ツリー上でマウススクロールが効かない問題への対処。
    /// 未処理のホイールを親へ再送し、外側 ScrollViewer に届ける。
    /// </summary>
    private void Tree_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject d)
            return;

        e.Handled = true;
        var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = sender,
        };
        if (VisualTreeHelper.GetParent(d) is UIElement parent)
            parent.RaiseEvent(forwarded);
    }

    private void OutlineRow_Click(object sender, MouseButtonEventArgs e)
    {
        // Range.Start ではなく SelectionRange.Start を使い、doc コメント／属性の先頭や宣言行の左端ではなく
        // シンボル名そのものへ着地させる。
        if (sender is FrameworkElement { DataContext: CodeOutlineItem item })
            SourceLocationActivated?.Invoke(
                this, new SourceLocationActivatedEventArgs(item.JumpLine1, item.JumpColumn0));
    }

    /// <summary>
    /// マウスで直接クリックした項目は既に表示範囲内にあるため、選択時に WPF が発行する
    /// <see cref="FrameworkElement.RequestBringIntoView"/> を止める。これを外側の ScrollViewer が
    /// 処理すると、深いノードを全幅表示しようとして EditorSupport が不要に右へスクロールする。
    /// キーボード操作等の表示要求はそのまま通す。
    /// </summary>
    private void OutlineItem_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (Mouse.LeftButton == MouseButtonState.Pressed)
            e.Handled = true;
    }

    private void CallRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CodeCallRow row } && row.CanJump)
            FileLocationActivated?.Invoke(this,
                new FileLocationActivatedEventArgs(row.Path!, row.Line1, row.Column0));
    }

    private void Install_Click(object sender, RoutedEventArgs e)
        => InstallRequested?.Invoke(this, EventArgs.Empty);

    private void Settings_Click(object sender, RoutedEventArgs e)
        => OpenLspSettingsRequested?.Invoke(this, EventArgs.Empty);

    private void Docs_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.NoticeDocsUrl is { Length: > 0 } url)
            OpenDocsRequested?.Invoke(this, url);
    }
}

/// <summary>アウトラインクリックのジャンプ先（1 始まり行＋0 始まり列）。</summary>
public sealed class SourceLocationActivatedEventArgs : EventArgs
{
    public SourceLocationActivatedEventArgs(int line1, int column0)
    {
        Line1 = line1;
        Column0 = column0;
    }

    public int Line1 { get; }
    public int Column0 { get; }
}

/// <summary>②パネル行クリックのジャンプ先（ローカルパス＋1 始まり行＋0 始まり列）。</summary>
public sealed class FileLocationActivatedEventArgs : EventArgs
{
    public FileLocationActivatedEventArgs(string path, int line1, int column0 = 0)
    {
        Path = path;
        Line1 = line1;
        Column0 = column0;
    }

    public string Path { get; }
    public int Line1 { get; }
    public int Column0 { get; }
}
