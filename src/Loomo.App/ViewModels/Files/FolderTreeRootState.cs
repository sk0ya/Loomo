using System.Collections.ObjectModel;
using System.Threading;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>複数フォルダーワークスペースで、ワークスペースフォルダー1件ぶんの表示状態をまとめる
/// （表示中サブフォルダー・ピン留め・Git状態・監視・進行中の読込）。単一フォルダー時は
/// <see cref="FolderTreeViewModel"/> が直接持つ既存フィールド（_currentRoot 等）を使うため、
/// このクラスは Folders.Count &gt; 1 のときだけインスタンス化される。</summary>
internal sealed class FolderTreeRootState
{
    /// <summary>このワークスペースフォルダーの固定ルート（Git/ピン状態の参照キー、RootKey と同じ値）。</summary>
    public string FolderPath { get; }

    /// <summary>ツリーに表示中のサブフォルダー（ピン切替で FolderPath 配下のフォルダへ変わり得る）。</summary>
    public string DisplayedPath { get; set; }

    /// <summary>このフォルダー内でピン留めしたサブフォルダー（正規化済みフルパス）。</summary>
    public HashSet<string> PinnedPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>ピン切替候補（先頭が FolderPath 自身、以降がピン留めフォルダー）。</summary>
    public ObservableCollection<FolderRootOption> RootOptions { get; } = new();

    public FolderRootOption? SelectedRootOption { get; set; }

    public GitTreeState GitState { get; set; } = GitTreeState.Empty;

    public DebouncedFolderWatcher? Watcher { get; set; }

    public CancellationTokenSource? GitLoadCts { get; set; }

    public Task GitLoadTask { get; set; } = Task.CompletedTask;

    /// <summary>ツリーに表示している見出しノード本体（Nodes に入っているインスタンス）。</summary>
    public FileNodeViewModel? HeaderNode { get; set; }

    public FolderTreeRootState(string folderPath)
    {
        FolderPath = folderPath;
        DisplayedPath = folderPath;
    }
}
