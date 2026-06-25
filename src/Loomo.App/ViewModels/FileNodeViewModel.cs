using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace sk0ya.Loomo.App.ViewModels;

public sealed partial class FileNodeViewModel : ObservableObject
{
    private readonly FolderTreeViewModel _owner;
    private bool _loaded;

    public string FullPath { get; }
    public string Name { get; }
    public bool IsDirectory { get; }

    // 拡張子から分類したベクターアイコン。形状は種別ごと、色はカテゴリ（コード/設定/マークアップ/
    // 画像/フォルダ/既定）で割り当てる。テーマに依らず一定（ファイル種別の色は固定が分かりやすい）。
    public Geometry IconGeometry { get; }
    public Brush IconBrush { get; }

    // HTML ファイルだけ「ブラウザで開く」コンテキストメニューを出すための判定。
    public bool IsHtml => !IsDirectory
        && (FullPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            || FullPath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase));

    public ObservableCollection<FileNodeViewModel> Children { get; } = new();

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    // git の差分マーク。XAML 側の DataTrigger が種別ごとに表示文字・色を割り当てる。
    [ObservableProperty] private GitChangeKind _gitStatus;

    // ピン留め済みか（コンテキストメニューの「ピン留め／解除」の出し分け）。
    // ピン状態の変更時は owner（RefreshPinMarks）が読込済みノードへ反映する。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPinnable))]
    private bool _isPinned;

    /// <summary>「ピン留め」メニューを出すか（フォルダかつ未ピン）。</summary>
    public bool IsPinnable => IsDirectory && !IsPinned;

    public FileNodeViewModel(string fullPath, bool isDirectory, FolderTreeViewModel owner)
    {
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Name = Path.GetFileName(fullPath);
        _owner = owner;
        GitStatus = owner.GitStatusFor(fullPath, isDirectory);
        if (isDirectory)
            _isPinned = owner.IsPinnedPath(fullPath);

        var iconKind = FileIcons.Classify(fullPath, isDirectory);
        IconGeometry = FileIcons.GeometryFor(iconKind);
        IconBrush = FileIcons.BrushFor(iconKind);

        if (isDirectory) Children.Add(Placeholder); // 遅延読込用ダミー
    }

    // 監視更新で git 状態が変わったとき、既存ノード（差分更新で再利用されるインスタンス）の
    // マークを最新へ更新する。
    public void RefreshGitStatus() => GitStatus = _owner.GitStatusFor(FullPath, IsDirectory);

    private static readonly FileNodeViewModel Placeholder = new();
    private FileNodeViewModel()
    {
        FullPath = "";
        Name = "";
        IsDirectory = false;
        _owner = null!;
        IconGeometry = FileIcons.GeometryFor(FileIconKind.Document);
        IconBrush = FileIcons.BrushFor(FileIconKind.Document);
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && IsDirectory && !_loaded)
        {
            _loaded = true;
            Children.Clear();
            foreach (var child in _owner.Children(FullPath))
                Children.Add(child);
        }
    }

    // 畳まれた枝を遅延読込前の状態へ戻す。監視更新で中身が古くなっても、次に展開したとき
    // 最新を読み直すため、ダミーの子だけを残して再読込可能にする。
    public void ResetToLazy()
    {
        if (!IsDirectory || !_loaded)
            return;

        _loaded = false;
        Children.Clear();
        Children.Add(Placeholder);
    }

    // フィルタ済みの子を先に流し込み、遅延読込を無効化する（展開しても再読込しない）。
    public void LoadChildren(IReadOnlyList<FileNodeViewModel> children)
    {
        _loaded = true;
        Children.Clear();
        foreach (var child in children)
            Children.Add(child);
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) _owner.NotifySelected(FullPath);
    }
}
