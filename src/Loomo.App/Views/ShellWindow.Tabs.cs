using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.Layout;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Services;
using Editor.Controls;
using Editor.Controls.Git;
using Editor.Controls.Themes;
using Terminal.Rendering;
using Terminal.Settings;
using Terminal.Tabs;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ターミナル／エディタのタブ管理（作成・選択・クローズ・プレビュータブ）</summary>
public partial class ShellWindow
{
    private void OnTerminalNewTab(object sender, RoutedEventArgs e)
    {
        var startDir = _activeTerminalTab?.View.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(startDir) || !Directory.Exists(startDir))
            startDir = _activeWorkspace?.RootPath ?? _terminal.CurrentDirectory;

        var tab = CreateTerminalTab(startDir);
        _terminalTabs.Add(tab);
        _vm.Tabs.AddTerminalTab(tab.Id, $"Terminal {CurrentTerminalWorkspace.NextTabNumber++}", false);
        ActivateTerminalTab(tab.Id);
        SaveActiveWorkspaceSnapshot();
    }

    private void OnTerminalTabSelected(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
            ActivateTerminalTab(id);
    }

    // タブを中ボタンクリックで閉じる（Terminal / Editor / Browser 共通）
    private async void OnTabMiddleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || sender is not FrameworkElement { Tag: Guid id })
            return;

        e.Handled = true;
        if (_terminalTabs.Any(t => t.Id == id))
            await CloseTerminalTabAsync(id);
        else if (_editorTabs.Any(t => t.Id == id))
            CloseEditorTab(id);
        else if (_browserTabs.Any(t => t.Id == id))
            await CloseBrowserTabAsync(id);
        else
            return;

        SaveActiveWorkspaceSnapshot();
    }

    private async void OnTerminalTabClosed(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
        {
            await CloseTerminalTabAsync(id);
            SaveActiveWorkspaceSnapshot();
        }
    }

    private void OnEditorNewTab(object sender, RoutedEventArgs e)
    {
        var tab = CreateEditorTab();
        _editorTabs.Add(tab);
        _vm.Tabs.AddEditorTab(tab.Id, null, false, false);
        ActivateEditorTab(tab.Id);
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>
    /// 仮想ドキュメント（システムプロンプト・危険コマンド一覧など）を編集するための専用タブを用意する。
    /// 同名タブが既にあればそれをアクティブ化して再利用し、無ければ新規タブを作成する。
    /// EditorService が <see cref="VimEditorControl.OpenVirtualDocument"/> を呼ぶ直前にこれを呼ぶため、
    /// ここでアクティブ化（＝Attach）した control に対して仮想ドキュメントが開かれる。
    /// </summary>
    private void OpenVirtualDocumentTab(string title)
    {
        var existing = _editorTabs.FirstOrDefault(t =>
            string.Equals(t.VirtualTitle, title, StringComparison.Ordinal));
        if (existing is not null)
        {
            ActivateEditorTab(existing.Id);
            return;
        }

        var tab = CreateEditorTab();
        tab.VirtualTitle = title;
        _editorTabs.Add(tab);
        _vm.Tabs.AddEditorTab(tab.Id, title, false, false);
        ActivateEditorTab(tab.Id);
        SaveActiveWorkspaceSnapshot();
    }

    private async Task OpenFileInNewEditorTabAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        var existing = _editorTabs.FirstOrDefault(t =>
            string.Equals(t.Control.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            // 明示的に開いた（ダブルクリック・Enter 等）ので、プレビュー中なら通常タブへ確定する。
            if (ReferenceEquals(_previewEditorTab, existing))
                SetPreviewTab(null);
            ActivateEditorTab(existing.Id);
            return;
        }

        var tab = CreateEditorTab();
        _editorTabs.Add(tab);
        _vm.Tabs.AddEditorTab(tab.Id, path, false, false);
        ActivateEditorTab(tab.Id);
        await _editor.OpenFileAsync(path);
        UpdateEditorTab(tab);
        // タブ活性化の時点では FilePath が未確定だったので、読込後に EditorSupport を同期し直す。
        await UpdateEditorSupportAsync();
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>
    /// FolderTree の単クリックでファイルをプレビュータブ（タイトル斜体）で開く。
    /// 未編集のプレビュータブ（無ければ空の Untitled タブ）を使い回して中身だけ差し替えるので、
    /// クリックのたびにタブが増えない。プレビュータブは編集された時点で通常タブへ昇格する
    /// （<see cref="UpdateEditorTab"/>）。既にタブで開いているファイルはそれをアクティブ化するだけ。
    /// </summary>
    private async Task OpenFileInPreviewTabAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        var existing = _editorTabs.FirstOrDefault(t =>
            string.Equals(t.Control.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActivateEditorTab(existing.Id);
            return;
        }

        // 差し替え先：未編集のプレビュータブ、無ければアクティブな空の Untitled タブを転用する。
        var target = _previewEditorTab is { } preview && _editorTabs.Contains(preview)
                     && !preview.Control.IsModified && !preview.Control.IsVirtualDocument
            ? preview
            : _activeEditorTab is { } active && _editorTabs.Contains(active)
              && string.IsNullOrEmpty(active.Control.FilePath) && !active.Control.IsModified
              && !active.Control.IsVirtualDocument && active.VirtualTitle is null
                ? active
                : null;

        if (target is null)
        {
            target = CreateEditorTab();
            _editorTabs.Add(target);
            _vm.Tabs.AddEditorTab(target.Id, path, false, false);
        }

        ActivateEditorTab(target.Id);
        await _editor.OpenFileAsync(path);
        // LoadFile 中の BufferChanged が UpdateEditorTab の昇格判定を誤爆させないよう、読込後に印を付ける。
        SetPreviewTab(target);
        UpdateEditorTab(target);
        await UpdateEditorSupportAsync();
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>プレビュータブの参照とタブUIの斜体表示を同期して切り替える（null で解除＝昇格）。</summary>
    private void SetPreviewTab(EditorTab? tab)
    {
        if (_previewEditorTab is { } old && !ReferenceEquals(old, tab))
            _vm.Tabs.SetEditorTabPreview(old.Id, false);
        _previewEditorTab = tab;
        if (tab is not null)
        {
            MovePreviewEditorTabToEnd();
            _vm.Tabs.SetEditorTabPreview(tab.Id, true);
        }
    }

    private void MovePreviewEditorTabToEnd()
    {
        if (_previewEditorTab is not { } preview)
            return;

        var index = _editorTabs.FindIndex(t => ReferenceEquals(t, preview));
        var last = _editorTabs.Count - 1;
        if (index < 0 || index == last)
            return;

        _editorTabs.RemoveAt(index);
        _editorTabs.Add(preview);
    }

    /// <summary>FolderTree の HTML をアプリ内ブラウザの新規タブで開く（file:// URL）。</summary>
    private async Task OpenFileInBrowserAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        await OpenUrlInBrowserAsync(new Uri(Path.GetFullPath(path)).AbsoluteUri, Path.GetFileName(path));
    }

    /// <summary>エディタ本文の URL クリック（Ctrl+Click / gx）を、OS 既定ブラウザではなく内蔵ブラウザペインで開く。</summary>
    private void OnEditorLinkClicked(object? sender, LinkClickedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Url))
            return;

        // 既定動作（Process.Start で OS のブラウザを開く）を抑止し、内蔵ブラウザで開く。
        e.Handled = true;
        _ = OpenUrlInBrowserAsync(e.Url, null);
    }

    /// <summary>
    /// エディタ本文のファイルパスクリック（Ctrl+Click / gx）を、現在ファイルまたはワークスペースを
    /// 基準に解決して Loomo のエディタタブで開く。
    /// </summary>
    private void OnEditorFileLinkClicked(object? sender, FileLinkClickedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Path))
            return;

        var currentPath = (sender as VimEditorControl)?.FilePath;
        if (!EditorFileLinkResolver.TryResolve(
                e.Path,
                currentPath,
                _workspace.RootPath,
                out var fullPath,
                out var line,
                out var column,
                out var isDirectory))
        {
            e.Handled = true;
            if (sender is VimEditorControl editor)
                editor.ShowStatusMessage($"ファイルが存在しません: {e.Path}");
            return;
        }

        e.Handled = true;
        if (isDirectory)
        {
            _workspace.SelectedPath = fullPath;
            return;
        }

        _ = OpenPathInEditorAsync(fullPath, line, column);
    }

    /// <summary>
    /// ターミナル本文のクリック（OSC 8 ハイパーリンク／検出した URL・ファイルパス）を Loomo で受け、
    /// 振り分ける（sk0ya.Terminal.Controls 1.0.12 は生テキスト <c>Target</c> を渡してくる）。
    /// http/https は内蔵ブラウザペインで、ファイルパス（必要なら :行[:列] 付き）はエディタで開く。
    /// それ以外（mailto: 等や、解決できないファイルパス）は Handled=false のままにして、
    /// ライブラリ既定の外部起動（Process.Start）に委ねる。
    /// </summary>
    private void OnTerminalLinkActivated(object? sender, TerminalHyperlinkActivatedEventArgs e)
    {
        var target = e.Target;
        if (string.IsNullOrWhiteSpace(target))
            return;

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            {
                // 既定動作（OS ブラウザ）を抑止し、内蔵ブラウザで開く。
                e.Handled = true;
                _ = OpenUrlInBrowserAsync(uri.AbsoluteUri, null);
                return;
            }

            if (uri.IsFile)
            {
                e.Handled = true;
                _ = OpenPathInEditorAsync(uri.LocalPath, line: 0, column: 0);
                return;
            }

            return; // mailto: 等は既定の外部起動に委ねる。
        }

        // 絶対 URI でなければファイルパスとして扱う（:行[:列] 付きを許容し、ターミナルの cwd で解決）。
        var cwd = (sender as TerminalTabView)?.WorkingDirectory;
        if (TryResolveFilePath(target, cwd, out var fullPath, out var line, out var column))
        {
            e.Handled = true;
            _ = OpenPathInEditorAsync(fullPath, line, column);
        }
        // 解決できなければ Handled=false のまま → ライブラリ既定の Process.Start に委ねる。
    }

    private static readonly System.Text.RegularExpressions.Regex TrailingLineColumn =
        new(@":(\d+)(?::(\d+))?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// ターミナルが検出したファイルパス文字列（末尾に :行[:列] が付くことがある）を、絶対パスと
    /// 行・列に分解する。相対パスは <paramref name="workingDirectory"/>（ターミナルの cwd）で解決し、
    /// 実在するファイルのときだけ <c>true</c> を返す。
    /// </summary>
    private static bool TryResolveFilePath(string target, string? workingDirectory, out string fullPath, out int line, out int column)
    {
        fullPath = "";
        line = 0;
        column = 0;

        var path = target;
        var match = TrailingLineColumn.Match(path);
        if (match.Success)
        {
            path = path[..match.Index];
            int.TryParse(match.Groups[1].Value, out line);
            if (match.Groups[2].Success)
                int.TryParse(match.Groups[2].Value, out column);
        }

        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            if (!Path.IsPathRooted(path))
            {
                if (string.IsNullOrWhiteSpace(workingDirectory))
                    return false;
                path = Path.Combine(workingDirectory, path);
            }

            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return File.Exists(fullPath);
    }

    /// <summary>ファイルをエディタの新規タブで開き、行・列が指定されていればそこへキャレットを移動する。</summary>
    private async Task OpenPathInEditorAsync(string fullPath, int line, int column)
    {
        await OpenFileInNewEditorTabAsync(fullPath);

        if (line <= 0)
            return;

        // 開いたタブがアクティブになっているので、そのコントロールでキャレットを移動する。
        if (_activeEditorTab is { } tab &&
            string.Equals(tab.Control.FilePath, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            tab.Control.NavigateTo(line, column > 0 ? column : 1);
        }
    }

    /// <summary>任意の URL をアプリ内ブラウザの新規タブで開く（必要ならブラウザペインを表示する）。</summary>
    private async Task OpenUrlInBrowserAsync(string url, string? title)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        // ブラウザペインが隠れていれば表示してから開く。
        if (!IsPaneVisible(PaneKind.Browser))
            SetPaneVisible(PaneKind.Browser, true);

        await CreateBrowserTabAsync(url, requestedTitle: title);
        SaveActiveWorkspaceSnapshot();
    }

    private void OnEditorTabSelected(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
            ActivateEditorTab(id);
    }

    private void OnEditorTabClosed(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
        {
            CloseEditorTab(id);
            SaveActiveWorkspaceSnapshot();
        }
    }

    private TerminalWorkspaceTabs CurrentTerminalWorkspace
        => _activeTerminalWorkspace ?? _scratchTerminalWorkspace;

    private EditorWorkspaceTabs CurrentEditorWorkspace
        => _activeEditorWorkspace ?? _scratchEditorWorkspace;

    private void ActivateTerminalTab(Guid id)
    {
        var tab = _terminalTabs.FirstOrDefault(t => t.Id == id);
        if (tab is null)
            return;

        _terminalViews?.Activate(id);

        _activeTerminalTab = tab;
        CurrentTerminalWorkspace.ActiveTabId = id;
        _terminal.Attach(tab.View);
        if (Directory.Exists(tab.View.WorkingDirectory))
            _terminal.SetWorkingDirectory(tab.View.WorkingDirectory);
        _vm.Tabs.ActivateTerminalTab(id);
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>フォーカスがビューポート間を移ったとき、タブ strip の強調と各サービスのアタッチを追従させる（再描画はしない）。</summary>
    private void SetActiveTerminalTab(TerminalTab tab)
    {
        _activeTerminalTab = tab;
        CurrentTerminalWorkspace.ActiveTabId = tab.Id;
        _terminal.Attach(tab.View);
        if (Directory.Exists(tab.View.WorkingDirectory))
            _terminal.SetWorkingDirectory(tab.View.WorkingDirectory);
        _vm.Tabs.ActivateTerminalTab(tab.Id);
    }

    private async Task CloseTerminalTabAsync(Guid id)
    {
        var index = _terminalTabs.FindIndex(t => t.Id == id);
        if (index < 0)
            return;

        var wasActive = _activeTerminalTab?.Id == id;
        var tab = _terminalTabs[index];
        PaneSplitView.Detach(tab.View);
        await tab.View.CloseAsync();
        _terminalTabs.RemoveAt(index);
        _vm.Tabs.RemoveTerminalTab(id);
        _terminalViews?.RemoveTab(id);
        ForgetTerminalActivity(id);

        if (_terminalTabs.Count == 0)
        {
            var startDir = _activeWorkspace?.RootPath ?? _terminal.CurrentDirectory;
            var newTab = CreateTerminalTab(startDir);
            _terminalTabs.Add(newTab);
            _vm.Tabs.AddTerminalTab(newTab.Id, "Terminal", false);
            ActivateTerminalTab(newTab.Id);
            return;
        }

        _terminalViews?.RepairTabs(_terminalTabs.Select(t => t.Id));

        if (wasActive)
        {
            ActivateTerminalTab(_terminalTabs[Math.Min(index, _terminalTabs.Count - 1)].Id);
        }
        else
        {
            _terminalViews?.Rebuild();
            if (_terminalViews?.FocusedTabId is { } fid && _terminalTabs.FirstOrDefault(t => t.Id == fid) is { } ft)
                SetActiveTerminalTab(ft);
        }
    }

    private void ActivateEditorTab(Guid id)
    {
        var tab = _editorTabs.FirstOrDefault(t => t.Id == id);
        if (tab is null)
            return;

        // フォーカス中ビューポートへこのタブを割り当てて再描画＋フォーカス（分割していなければ単一ビューポート）。
        _editorViews?.Activate(id);

        _activeEditorTab = tab;
        CurrentEditorWorkspace.ActiveTabId = id;
        _editor.Attach(tab.Control);
        _vm.Tabs.ActivateEditorTab(id);
        QueueEditorTabHeaderIntoView(id);
        _ = SwitchEditorSupportSourceAsync(tab);
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>フォーカスがビューポート間を移ったとき、タブ strip の強調と各サービスのアタッチを追従させる（再描画はしない）。</summary>
    private void SetActiveEditorTab(EditorTab tab)
    {
        _activeEditorTab = tab;
        CurrentEditorWorkspace.ActiveTabId = tab.Id;
        _editor.Attach(tab.Control);
        _vm.Tabs.ActivateEditorTab(tab.Id);
        QueueEditorTabHeaderIntoView(tab.Id);
        _ = SwitchEditorSupportSourceAsync(tab);
    }

    private void QueueEditorTabHeaderIntoView(Guid id)
    {
        Dispatcher.BeginInvoke(
            new Action(() => ScrollEditorTabHeaderIntoView(id)),
            DispatcherPriority.Loaded);
    }

    private void ScrollEditorTabHeaderIntoView(Guid id)
    {
        if (EditorTabStripScrollViewer.ViewportWidth <= 0)
            return;

        EditorTabStripItems.UpdateLayout();
        if (FindEditorTabHeader(id, EditorTabStripItems) is not { } header)
            return;

        var bounds = header.TransformToAncestor(EditorTabStripScrollViewer)
            .TransformBounds(new Rect(0, 0, header.ActualWidth, header.ActualHeight));

        if (bounds.Left < 0)
        {
            EditorTabStripScrollViewer.ScrollToHorizontalOffset(
                Math.Max(0, EditorTabStripScrollViewer.HorizontalOffset + bounds.Left));
        }
        else if (bounds.Right > EditorTabStripScrollViewer.ViewportWidth)
        {
            EditorTabStripScrollViewer.ScrollToHorizontalOffset(
                EditorTabStripScrollViewer.HorizontalOffset + bounds.Right - EditorTabStripScrollViewer.ViewportWidth);
        }
    }

    private static FrameworkElement? FindEditorTabHeader(Guid id, DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { DataContext: TabEntryViewModel tab } element && tab.Id == id)
                return element;

            if (FindEditorTabHeader(id, child) is { } found)
                return found;
        }

        return null;
    }

    private void CloseEditorTab(Guid id)
    {
        var index = _editorTabs.FindIndex(t => t.Id == id);
        if (index < 0)
            return;

        var wasActive = _activeEditorTab?.Id == id;
        var tab = _editorTabs[index];
        if (ReferenceEquals(_editorSupportSourceTab, tab))
        {
            _editorSupportDebounceTimer?.Stop();
            DetachEditorSupportSource();
            _editorSupportSourcePinned = false;
            UpdateEditorSupportPinToggle();
        }
        PaneSplitView.Detach(tab.Control);
        if (ReferenceEquals(_previewEditorTab, tab))
            _previewEditorTab = null;
        _editorTabs.RemoveAt(index);
        _vm.Tabs.RemoveEditorTab(id);
        _editorViews?.RemoveTab(id);

        if (_editorTabs.Count == 0)
        {
            var newTab = CreateEditorTab();
            _editorTabs.Add(newTab);
            _vm.Tabs.AddEditorTab(newTab.Id, null, false, false);
            ActivateEditorTab(newTab.Id);
            return;
        }

        _editorViews?.RepairTabs(_editorTabs.Select(t => t.Id));

        if (wasActive)
        {
            ActivateEditorTab(_editorTabs[Math.Min(index, _editorTabs.Count - 1)].Id);
        }
        else
        {
            _editorViews?.Rebuild();
            if (_editorViews?.FocusedTabId is { } fid && _editorTabs.FirstOrDefault(t => t.Id == fid) is { } ft)
                SetActiveEditorTab(ft);
        }
    }

}
