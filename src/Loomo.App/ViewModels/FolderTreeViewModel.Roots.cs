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

/// <summary>FolderTreeViewModel のルート/ピン管理パート：ワークスペースルート（単一フォルダー時）と
/// ワークスペースフォルダーごと（複数フォルダー時）のピン留めフォルダの切替候補（RootOptions）の
/// 構築・選択、表示ルートの差し替え、ピンマークの反映。</summary>
public sealed partial class FolderTreeViewModel
{
    /// <summary>fullPath がピン留め済みか（属するワークスペースフォルダー基準）。</summary>
    public bool IsPinnedPath(string fullPath)
    {
        if (fullPath.Length == 0)
            return false;
        var full = Path.GetFullPath(fullPath);

        if (_multiRootStates.Count == 0)
            return _pinnedPaths.Contains(full);

        var state = ResolveStateForPath(full);
        return state is not null && state.PinnedPaths.Contains(full);
    }

    /// <summary>fullPath を含むワークスペースフォルダーの状態を返す（複数フォルダー時のみ。
    /// 単一フォルダー時は null——呼び出し側は _pinnedPaths 等の単一フィールドを直接使う）。</summary>
    private FolderTreeRootState? ResolveStateForPath(string fullPath)
    {
        foreach (var state in _multiRootStates.Values)
            if (PathsEqual(fullPath, state.FolderPath)
                || fullPath.StartsWith(state.FolderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return state;
        return null;
    }

    /// <summary>フォルダをピン留めし、そのフォルダーが属するルート（単一フォルダー時はワークスペース
    /// ルート、複数フォルダー時は所属するワークスペースフォルダー）の切替候補へ追加する。</summary>
    public void PinFolder(string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        if (!_query.DirectoryExists(full))
            return;

        if (_multiRootStates.Count == 0)
        {
            if (_workspaceRoot is null || PathsEqual(full, _workspaceRoot) || IsPinnedPath(full))
                return;

            RootOptions.Add(new FolderRootOption(full, LabelFor(full), isPinned: true));
            RebuildPinnedSet();
            RefreshPinMarks();
            RootStateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var state = ResolveStateForPath(full);
        if (state is null || PathsEqual(full, state.FolderPath) || IsPinnedPath(full))
            return;

        state.RootOptions.Add(new FolderRootOption(full, LabelForWithin(state.FolderPath, full), isPinned: true));
        RebuildPinnedSet(state);
        RefreshPinMarks();
        RootStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>ピン留めを解除する。表示中のパスだった場合は、そのルートの表示をルート自身へ戻す。</summary>
    public void UnpinFolder(string fullPath)
    {
        var full = Path.GetFullPath(fullPath);

        if (_multiRootStates.Count == 0)
        {
            var option = RootOptions.FirstOrDefault(o => o.IsPinned && PathsEqual(o.FullPath, full));
            if (option is null)
                return;

            var wasCurrent = _currentRoot is not null && PathsEqual(option.FullPath, _currentRoot);
            RootOptions.Remove(option);
            RebuildPinnedSet();
            RefreshPinMarks();

            if (wasCurrent && RootOptions.Count > 0)
                SelectRootOption(RootOptions[0]);

            RootStateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var state = ResolveStateForPath(full);
        if (state is null)
            return;

        var opt = state.RootOptions.FirstOrDefault(o => o.IsPinned && PathsEqual(o.FullPath, full));
        if (opt is null)
            return;

        var wasDisplayed = PathsEqual(opt.FullPath, state.DisplayedPath);
        state.RootOptions.Remove(opt);
        RebuildPinnedSet(state);
        RefreshPinMarks();

        if (wasDisplayed && state.RootOptions.Count > 0)
            SwitchRootOption(state, state.RootOptions[0]);

        RootStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>ワークスペースから取り除く（複数フォルダー時、見出しの右クリックから）。</summary>
    public void RemoveFromWorkspace(FileNodeViewModel node)
    {
        if (node.IsWorkspaceFolderRoot)
            _workspace.RemoveFolder(node.RootKey);
    }

    /// <summary>フォルダーをワークスペースへ追加する（「フォルダーをワークスペースに追加」ボタン）。
    /// 実在しない・既存フォルダーと祖先/子孫関係のパスは WorkspaceService 側で無視される。</summary>
    public void AddFolderToWorkspace(string path)
    {
        if (_query.DirectoryExists(path))
            _workspace.AddFolder(path);
    }

    /// <summary>スナップショット保存用：プライマリ以外のワークスペースフォルダーごとの
    /// パス・ピン留め・表示中サブフォルダー。単一フォルダー時は空。</summary>
    public IReadOnlyList<WorkspaceFolderPin> CaptureAdditionalFolders()
    {
        if (_multiRootStates.Count == 0)
            return Array.Empty<WorkspaceFolderPin>();

        var result = new List<WorkspaceFolderPin>();
        foreach (var folder in _workspace.Folders.Skip(1))
        {
            if (!_multiRootStates.TryGetValue(folder, out var state))
                continue;

            result.Add(new WorkspaceFolderPin
            {
                FolderPath = state.FolderPath,
                PinnedFolders = state.RootOptions.Where(o => o.IsPinned).Select(o => o.FullPath).ToList(),
                TreeRootPath = PathsEqual(state.DisplayedPath, state.FolderPath) ? null : state.DisplayedPath,
            });
        }
        return result;
    }

    /// <summary>スナップショット復元用：保存されていたプライマリ以外のワークスペースフォルダーを
    /// 追加し、フォルダーごとのピン留め・表示中サブフォルダーを復元する。LoadRoot の直後に呼ぶ。
    /// LoadRoot 同様、復元中は RootStateChanged を発火しない（保存イベントの多重発火を避ける）。</summary>
    public void RestoreAdditionalFolders(IReadOnlyList<WorkspaceFolderPin> pins)
    {
        if (pins.Count == 0)
            return;

        _suppressFoldersChangedReaction = true;
        try
        {
            foreach (var pin in pins)
                if (!string.IsNullOrWhiteSpace(pin.FolderPath) && _query.DirectoryExists(pin.FolderPath))
                    _workspace.AddFolder(pin.FolderPath);
        }
        finally
        {
            _suppressFoldersChangedReaction = false;
        }

        ReconcileRootStates();

        foreach (var pin in pins)
        {
            if (string.IsNullOrWhiteSpace(pin.FolderPath) || !_multiRootStates.TryGetValue(
                    Path.GetFullPath(pin.FolderPath), out var state))
                continue;

            foreach (var pinnedSub in pin.PinnedFolders)
            {
                if (string.IsNullOrWhiteSpace(pinnedSub))
                    continue;
                var subFull = Path.GetFullPath(pinnedSub);
                if (PathsEqual(subFull, state.FolderPath) || !_query.DirectoryExists(subFull))
                    continue;
                if (state.RootOptions.Any(o => PathsEqual(o.FullPath, subFull)))
                    continue;
                state.RootOptions.Add(new FolderRootOption(subFull, LabelForWithin(state.FolderPath, subFull), isPinned: true));
            }
            RebuildPinnedSet(state);

            if (pin.TreeRootPath is { Length: > 0 } treeRoot)
            {
                var option = state.RootOptions.FirstOrDefault(o => PathsEqual(o.FullPath, treeRoot));
                if (option is not null)
                    SwitchRootOption(state, option);
            }
        }

        RefreshPinMarks();
    }

    [RelayCommand]
    private void UnpinCurrentRoot()
    {
        if (_multiRootStates.Count != 0 || SelectedRootOption is not { IsPinned: true } option)
            return;

        RootOptions.Remove(option);
        RebuildPinnedSet();
        RefreshPinMarks();

        if (RootOptions.Count > 0)
            SelectRootOption(RootOptions[0]);

        RootStateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ComboBox からの切替（単一フォルダー時のみ使う）。候補再構築中（_suppressRootSelection）や、
    // 解除で選択が一時的に null になったときは何もしない。
    partial void OnSelectedRootOptionChanged(FolderRootOption? value)
    {
        if (_suppressRootSelection || value is null)
            return;
        if (_currentRoot is not null && PathsEqual(value.FullPath, _currentRoot))
            return;

        SetDisplayRoot(value.FullPath);
        RootStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>複数フォルダー時、見出しの右クリックメニュー（ピン留めフォルダーへ切替）から呼ばれる。</summary>
    public void SwitchRootOption(FileNodeViewModel headerNode, FolderRootOption option)
    {
        if (!_multiRootStates.TryGetValue(headerNode.RootKey, out var state))
            return;
        SwitchRootOption(state, option);
        RootStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>見出しノードの切替候補（ワークスペースフォルダー自身＋そのフォルダー内のピン留め）。
    /// 単一フォルダー時／該当なしは空。「ピン留めフォルダーへ切替」サブメニューの構築に使う。</summary>
    public IReadOnlyList<FolderRootOption> RootOptionsFor(FileNodeViewModel headerNode)
        => _multiRootStates.TryGetValue(headerNode.RootKey, out var state)
            ? state.RootOptions
            : Array.Empty<FolderRootOption>();

    /// <summary>見出しノードが現在表示中の候補（サブメニューのチェックマーク表示に使う）。</summary>
    public FolderRootOption? SelectedRootOptionFor(FileNodeViewModel headerNode)
        => _multiRootStates.TryGetValue(headerNode.RootKey, out var state) ? state.SelectedRootOption : null;

    // RootStateChanged は呼び出し側（保存イベントを出したい経路）が明示的に発火する
    // （復元経路 RestoreAdditionalFolders は発火させたくないため、ここでは出さない）。
    private void SwitchRootOption(FolderTreeRootState state, FolderRootOption option)
    {
        state.SelectedRootOption = option;
        var full = Path.GetFullPath(option.FullPath);
        if (PathsEqual(full, state.DisplayedPath))
            return;

        state.DisplayedPath = full;
        state.Watcher?.Dispose();
        state.Watcher = null;

        RebuildMultiRootNodes();
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
                if (PathsEqual(full, _workspaceRoot!) || !_query.DirectoryExists(full))
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

    private static void RebuildPinnedSet(FolderTreeRootState state)
        => state.PinnedPaths = new HashSet<string>(
            state.RootOptions.Where(o => o.IsPinned).Select(o => o.FullPath),
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

    // ツリーの表示ルートを差し替える（ワークスペースの確定はしない、単一フォルダー時のみ使う）。
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
    private string LabelFor(string fullPath) => LabelForWithin(_workspaceRoot, fullPath);

    // LabelFor の汎用版：baseRoot 配下なら相対パス、そうでなければフルパス。複数フォルダー時は
    // baseRoot にそのワークスペースフォルダー自身のパスを渡す（プライマリ基準の相対化はしない）。
    private static string LabelForWithin(string? baseRoot, string fullPath)
    {
        var name = Path.GetFileName(fullPath.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(name))
            name = fullPath;

        if (baseRoot is null || PathsEqual(fullPath, baseRoot))
            return name;

        var relative = Path.GetRelativePath(baseRoot, fullPath);
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
