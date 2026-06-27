using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualBasic.FileIO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Agent;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>FolderTreeViewModel のルート/ピン管理パート：ワークスペースルートとピン留めフォルダの
/// 切替候補（RootOptions）の構築・選択、表示ルートの差し替え、ピンマークの反映。</summary>
public sealed partial class FolderTreeViewModel
{
    public bool IsPinnedPath(string fullPath)
        => fullPath.Length > 0 && _pinnedPaths.Contains(Path.GetFullPath(fullPath));

    /// <summary>フォルダをピン留めし、ルート切替候補へ追加する。</summary>
    public void PinFolder(string fullPath)
    {
        if (_workspaceRoot is null)
            return;

        var full = Path.GetFullPath(fullPath);
        if (PathsEqual(full, _workspaceRoot) || IsPinnedPath(full) || !Directory.Exists(full))
            return;

        RootOptions.Add(new FolderRootOption(full, LabelFor(full), isPinned: true));
        RebuildPinnedSet();
        RefreshPinMarks();
        RootStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>ピン留めを解除する。表示中ルートだった場合はワークスペースルートへ戻す。</summary>
    public void UnpinFolder(string fullPath)
    {
        var option = RootOptions.FirstOrDefault(o => o.IsPinned && PathsEqual(o.FullPath, fullPath));
        if (option is null)
            return;

        var wasCurrent = _currentRoot is not null && PathsEqual(option.FullPath, _currentRoot);
        RootOptions.Remove(option);
        RebuildPinnedSet();
        RefreshPinMarks();

        if (wasCurrent && RootOptions.Count > 0)
            SelectRootOption(RootOptions[0]);

        RootStateChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void UnpinCurrentRoot()
    {
        if (SelectedRootOption is { IsPinned: true } option)
            UnpinFolder(option.FullPath);
    }

    // ComboBox からの切替。候補再構築中（_suppressRootSelection）や、解除で選択が一時的に
    // null になったときは何もしない。
    partial void OnSelectedRootOptionChanged(FolderRootOption? value)
    {
        if (_suppressRootSelection || value is null)
            return;
        if (_currentRoot is not null && PathsEqual(value.FullPath, _currentRoot))
            return;

        SetDisplayRoot(value.FullPath);
        RootStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BuildRootOptions(IEnumerable<string> pinnedFolders)
    {
        _suppressRootSelection = true;
        try
        {
            RootOptions.Clear();
            RootOptions.Add(new FolderRootOption(_workspaceRoot!, LabelFor(_workspaceRoot!), isPinned: false));

            // 消えたピン・ルートと同一のピンは読み込み時に捨てる（保存時に自然に消える）。
            foreach (var pin in pinnedFolders)
            {
                if (string.IsNullOrWhiteSpace(pin))
                    continue;
                var full = Path.GetFullPath(pin);
                if (PathsEqual(full, _workspaceRoot!) || !Directory.Exists(full))
                    continue;
                if (RootOptions.Any(o => PathsEqual(o.FullPath, full)))
                    continue;
                RootOptions.Add(new FolderRootOption(full, LabelFor(full), isPinned: true));
            }
        }
        finally
        {
            _suppressRootSelection = false;
        }

        RebuildPinnedSet();
    }

    private void RebuildPinnedSet()
        => _pinnedPaths = new HashSet<string>(
            RootOptions.Where(o => o.IsPinned).Select(o => o.FullPath),
            StringComparer.OrdinalIgnoreCase);

    private FolderRootOption? FindRootOption(string fullPath)
    {
        try
        {
            var full = Path.GetFullPath(fullPath);
            return RootOptions.FirstOrDefault(o => PathsEqual(o.FullPath, full));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;   // 保存データの不正パスは無視してルートへ戻す
        }
    }

    // 選択（ComboBox 表示）と表示ルートを同期させる。OnSelectedRootOptionChanged 経由の
    // 二重切替・保存イベントは抑止する（呼び出し元が必要なら明示的に発火する）。
    private void SelectRootOption(FolderRootOption option)
    {
        _suppressRootSelection = true;
        try
        {
            SelectedRootOption = option;
        }
        finally
        {
            _suppressRootSelection = false;
        }

        if (_currentRoot is null || !PathsEqual(option.FullPath, _currentRoot))
            SetDisplayRoot(option.FullPath);
    }

    // ツリーの表示ルートを差し替える（ワークスペースの確定はしない）。
    private void SetDisplayRoot(string path)
    {
        _currentRoot = Path.GetFullPath(path);
        RootLabel = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(RootLabel)) RootLabel = path;

        // 検索パネルの既定の開始フォルダーを表示ルートへ追従させる。
        CurrentRootChanged?.Invoke(this, _currentRoot);

        // 旧ルートの内容を残さない（git 読込の継続でこのルートのツリーが投入される）。
        // ignore 非表示・差分マークは git に依存するため、空 git でフル描画して直後に
        // 訂正するより、git 完了後の 1 回の ReloadNodes でちらつきなく投入する。
        Nodes.Clear();
        HasVisibleNodes = false;
        EmptyMessage = "";

        // git 状態はバックグラウンドで読み、完了後に ReloadNodes でツリーへ反映する。
        // UI スレッドで git プロセスを同期起動しないので、ワークスペース切替が固まらない。
        RefreshGitStateAsync();
        StartWatching(_currentRoot);
    }

    // ComboBox の表示名。ワークスペースルートはフォルダ名、ピンはルートからの相対パス
    // （同名フォルダの区別がつくように）。ルート外・ドライブ違いはフルパスのまま。
    private string LabelFor(string fullPath)
    {
        var name = Path.GetFileName(fullPath.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(name))
            name = fullPath;

        if (_workspaceRoot is null || PathsEqual(fullPath, _workspaceRoot))
            return name;

        var relative = Path.GetRelativePath(_workspaceRoot, fullPath);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? fullPath
            : relative;
    }

    // ピン状態の変化を、読込済みノードの IsPinned（コンテキストメニューの出し分け）へ反映する。
    private void RefreshPinMarks()
    {
        static IEnumerable<FileNodeViewModel> Walk(IEnumerable<FileNodeViewModel> nodes)
        {
            foreach (var node in nodes)
            {
                yield return node;
                foreach (var child in Walk(node.Children))
                    yield return child;
            }
        }

        foreach (var node in Walk(Nodes))
            if (node.IsDirectory && node.FullPath.Length > 0)
                node.IsPinned = IsPinnedPath(node.FullPath);
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd('\\', '/'),
            Path.GetFullPath(b).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
}

