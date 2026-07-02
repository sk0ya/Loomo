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
        ViewportTree.Detach(tab.View);
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
        RecordTrailEditorTab(tab);
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
        RecordTrailEditorTab(tab);
        // 開いたファイルの拡張子に対応する言語サーバーが未導入/未設定なら、エディタ上端で導入を促す。
        _vm.LspPrompt.EvaluateForFile(tab.Control.FilePath);
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
        // 実体化済みのときだけ視覚ツリーから外して破棄する（未実体化タブは VimEditorControl を
        // 持たず、触れると無駄に実体化してしまう）。Dispose は LSP（言語サーバープロセス）と
        // ファイル監視を解放する。これは Unloaded では行われない（ワークスペース切替の一時的な
        // デタッチでも Unloaded は発火するため、ライブラリ側で破棄をホスト責務に分離した）。
        if (tab.IsRealized)
        {
            ViewportTree.Detach(tab.Control);
            tab.Control.Dispose();
        }
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

    /// <summary>FolderTree でファイル／フォルダがリネームされたとき、開いているエディタタブのパス・タブ名を
    /// 新パスへ追従させる。フォルダのリネームでは配下のファイルを開いたタブもまとめて付け替える。</summary>
    private void OnFolderTreeEntryRenamed(EntryRenamedEventArgs e)
    {
        foreach (var tab in _editorTabs)
        {
            var path = tab.PeekFilePath;
            if (string.IsNullOrEmpty(path))
                continue;

            string? newPath = null;
            if (e.IsDirectory)
            {
                if (IsPathUnder(path, e.OldPath))
                    newPath = Path.GetFullPath(Path.Combine(e.NewPath, Path.GetRelativePath(e.OldPath, path)));
            }
            else if (PathsEqual(path, e.OldPath))
            {
                newPath = e.NewPath;
            }

            if (newPath is not null)
                RebaseEditorTabPath(tab, newPath);
        }
    }

    /// <summary>エディタタブのファイルパスを差し替える（本文・Undo・未保存編集を保ったまま追従させる）。
    /// 未実体化タブは復元用スナップショットのパスだけ書き換え、開くときは新パスから読み込ませる。</summary>
    private void RebaseEditorTabPath(EditorTab tab, string newPath)
    {
        if (tab.IsRealized)
        {
            // バッファのパスを差し替えるだけ（LoadFile による再読込はカーソル・Undo・未保存編集を失うため避ける）。
            tab.Control.Engine.CurrentBuffer.FilePath = newPath;
            UpdateEditorTab(tab);   // タブ名更新＋スナップショット保存
        }
        else if (tab.Pending is { } pending)
        {
            pending.FilePath = newPath;
            pending.Title = Path.GetFileName(newPath);
            _vm.Tabs.UpdateEditorTab(tab.Id, newPath, pending.IsModified);
            SaveActiveWorkspaceSnapshot();
        }
    }

    /// <summary>FolderTree でファイル／フォルダが削除されたとき、該当（フォルダなら配下）の
    /// エディタタブを閉じる。</summary>
    private void OnFolderTreeEntryDeleted(string deletedPath)
    {
        var affected = _editorTabs
            .Where(t => t.PeekFilePath is { Length: > 0 } p
                && (PathsEqual(p, deletedPath) || IsPathUnder(p, deletedPath)))
            .Select(t => t.Id)
            .ToList();

        if (affected.Count == 0)
            return;

        foreach (var id in affected)
            CloseEditorTab(id);
        SaveActiveWorkspaceSnapshot();
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd('\\', '/'),
            Path.GetFullPath(b).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    // path が directory 配下か（directory 自身は含まない）。
    private static bool IsPathUnder(string path, string directory)
    {
        var dir = Path.GetFullPath(directory).TrimEnd('\\', '/');
        var full = Path.GetFullPath(path);
        return full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(dir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

}
