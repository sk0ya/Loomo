using System;
using System.IO;
using System.Linq;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.Services;

/// <summary>
/// Git 操作の対象フォルダー（<see cref="IWorkspaceService.Folders"/> のいずれか1つ）を保持する。
/// 明示選択が無ければプライマリ（<see cref="IWorkspaceService.PrimaryFolder"/>）。マルチルートで Git パネル／
/// セッションが「今どのリポジトリを見ているか」を切り替えられるようにする単一の真実源
/// （<see cref="GitCommandRunner"/> 等の下位サービスはすべてこれ経由でワークディレクトリを決める）。
/// </summary>
public sealed class GitRootState
{
    private readonly IWorkspaceService? _workspace;
    private readonly string? _fixedRoot;
    private string? _explicitRoot;

    public GitRootState(IWorkspaceService workspace)
    {
        _workspace = workspace;
        _workspace.FoldersChanged += (_, _) => Reconcile();
    }

    /// <summary>対象を<b>固定</b>した状態（§40.5）。<see cref="GitService.For"/> がリポジトリごとに作る
    /// <see cref="GitService"/> はこれを持ち、切替（<see cref="SetRoot"/>／<see cref="SelectForPath"/>）を受け付けない
    /// ——リポジトリを明示して得た窓口の対象が、よその操作で入れ替わってはいけないため。</summary>
    internal GitRootState(string fixedRoot) => _fixedRoot = fixedRoot;

    /// <summary>対象が固定されているか（<see cref="GitService.For"/> で得た窓口か）。</summary>
    public bool IsFixed => _fixedRoot is not null;

    /// <summary>対象フォルダーが変わったとき（明示切替・フォルダー追加/削除でプライマリへ戻ったとき等）。</summary>
    public event EventHandler? Changed;

    /// <summary>現在の Git 操作対象フォルダー。明示選択が無い／無効になったらプライマリ。</summary>
    public string? CurrentRoot => _fixedRoot ?? _explicitRoot ?? _workspace?.PrimaryFolder;

    /// <summary>対象フォルダーを明示的に切り替える。<paramref name="path"/> がワークスペースフォルダーで
    /// なければ無視する。プライマリを指定した場合は「明示選択なし」と同じ扱いにする（<see cref="OpenFolder"/>
    /// によるプライマリの入れ替えへ自然に追従させるため）。</summary>
    public void SetRoot(string? path)
    {
        if (IsFixed) return;
        var normalized = NormalizeIfValid(path);
        if (string.Equals(normalized, _explicitRoot, StringComparison.OrdinalIgnoreCase))
            return;
        _explicitRoot = normalized;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>fullPath を含むワークスペースフォルダーへ対象を切り替える（該当が無ければ何もしない）。
    /// ブレーム・履歴などファイル起点の Git 操作が、現在の対象と違うフォルダーのファイルを指したときに使う。</summary>
    public void SelectForPath(string fullPath)
    {
        if (_workspace is null) return;
        var full = Path.GetFullPath(fullPath);
        var folder = _workspace.Folders
            .Where(f => IsWithin(f, full))
            .OrderByDescending(f => f.Length)
            .FirstOrDefault();
        if (folder is not null)
            SetRoot(folder);
    }

    private string? NormalizeIfValid(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (_workspace is null)
            return null;
        var full = Path.GetFullPath(path);
        if (string.Equals(full, _workspace.PrimaryFolder, StringComparison.OrdinalIgnoreCase))
            return null;
        return _workspace.Folders.Any(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase))
            ? full
            : null;
    }

    private void Reconcile()
    {
        if (_workspace is null) return;
        if (_explicitRoot is not null
            && !_workspace.Folders.Any(f => string.Equals(f, _explicitRoot, StringComparison.OrdinalIgnoreCase)))
            _explicitRoot = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsWithin(string root, string candidate)
        => WorkspacePaths.IsWithin(root, candidate);
}
