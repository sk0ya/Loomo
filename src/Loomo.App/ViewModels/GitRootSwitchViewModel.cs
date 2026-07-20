using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>マルチルート時、Git 操作の対象フォルダーを選ぶ ComboBox の1候補。</summary>
public sealed record GitRootOption(string FullPath, string Label);

/// <summary>
/// Git 操作の対象フォルダー（<see cref="GitService.RootPath"/>）の切替 UI 状態。<see cref="GitService"/> と
/// 同じく Singleton——サイドバー Git パネル（<see cref="GitPanelViewModel"/>）とセッションペイン
/// （<see cref="GitSessionViewModel"/>）は別々の画面領域だが同じ対象リポジトリを見ているので、
/// どちらで切り替えても両方に反映されるよう、この状態を共有する（重複実装を避ける）。
/// </summary>
public sealed partial class GitRootSwitchViewModel : ObservableObject
{
    private readonly GitService _git;
    private readonly IWorkspaceService _workspace;
    private bool _suppressSelection;

    [ObservableProperty] private IReadOnlyList<GitRootOption> _rootOptions = Array.Empty<GitRootOption>();
    [ObservableProperty] private GitRootOption? _selectedRootOption;

    /// <summary>マルチルート（ワークスペースフォルダーが2つ以上）か。true のときだけ切替 ComboBox を出す。</summary>
    public bool IsMultiRoot => _workspace.Folders.Count > 1;

    public GitRootSwitchViewModel(GitService git, IWorkspaceService workspace)
    {
        _git = git;
        _workspace = workspace;
        _git.ActiveRootChanged += (_, _) => SyncSelection();
        _workspace.FoldersChanged += (_, _) => Rebuild();
        Rebuild();
    }

    // ComboBox の候補（ワークスペースフォルダー一覧）を組み立て直す。フォルダー追加/削除のたびに呼ぶ。
    private void Rebuild()
    {
        _suppressSelection = true;
        try
        {
            RootOptions = _workspace.Folders.Select(f => new GitRootOption(f, LabelFor(f))).ToArray();
            SelectedRootOption = RootOptions.FirstOrDefault(o =>
                string.Equals(o.FullPath, _git.RootPath, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _suppressSelection = false;
        }
        OnPropertyChanged(nameof(IsMultiRoot));
    }

    private static string LabelFor(string fullPath)
    {
        var name = System.IO.Path.GetFileName(fullPath.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? fullPath : name;
    }

    // ComboBox からの明示切替。GitService.ActiveRootChanged 経由の反映（SyncSelection）による選択反映では
    // 往復切替させない。
    partial void OnSelectedRootOptionChanged(GitRootOption? value)
    {
        if (_suppressSelection || value is null)
            return;
        _git.SetActiveRoot(value.FullPath);
    }

    // 対象フォルダーが切り替わったら（他経路の明示切替・ファイル起点の自動切替）ComboBox の選択を追従させる。
    private void SyncSelection()
    {
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            _suppressSelection = true;
            try
            {
                SelectedRootOption = RootOptions.FirstOrDefault(o =>
                    string.Equals(o.FullPath, _git.RootPath, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _suppressSelection = false;
            }
        }));
    }
}
