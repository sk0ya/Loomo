using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    /// <summary>アウトラインのメンバー名／↦ クリック：ソース行（1 始まり）へジャンプ。</summary>
    public event EventHandler<int>? SourceLineActivated;

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

    /// <summary>言語サーバー未接続／未導入の案内を表示する。</summary>
    internal void ShowNotice(LspNoticeModel.Notice notice)
        => _vm.ShowNotice(notice);

    private void OutlineRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CodeOutlineItem item })
            SourceLineActivated?.Invoke(this, item.DataLine1);
    }

    private void CallRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CodeCallRow row } && row.CanJump)
            FileLocationActivated?.Invoke(this, new FileLocationActivatedEventArgs(row.Path!, row.Line1));
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

/// <summary>②パネル行クリックのジャンプ先（ローカルパス＋1 始まり行）。</summary>
public sealed class FileLocationActivatedEventArgs : EventArgs
{
    public FileLocationActivatedEventArgs(string path, int line1)
    {
        Path = path;
        Line1 = line1;
    }

    public string Path { get; }
    public int Line1 { get; }
}
