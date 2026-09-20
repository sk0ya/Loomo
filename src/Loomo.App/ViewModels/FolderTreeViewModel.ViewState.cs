using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>FolderTreeViewModel の表示状態（展開・選択）の保存／復元パート。
/// ツリーは git 読込の完了後に非同期で投入されるため、復元値は「保留」として先に受け取り、
/// ルートごとの初回投入時に適用して消費する（適用後の監視更新で、利用者が畳んだ枝を開き直さない）。</summary>
public sealed partial class FolderTreeViewModel
{
    // 展開中フォルダーの保存上限。「すべて展開」後に離れても、スナップショットが肥大しないようにする。
    private const int MaxSavedExpandedPaths = 2000;

    private HashSet<string> _pendingExpandedPaths = new(StringComparer.OrdinalIgnoreCase);
    private string? _pendingSelectedPath;

    /// <summary>復元で選択するノードが決まったとき（IsSelected を立てる直前）。View が購読し、
    /// プログラムからの選択としてプレビューを開かず、表示位置までスクロールする。</summary>
    public event EventHandler<FileNodeViewModel>? SelectionRestored;

    /// <summary>次のツリー投入で復元する展開・選択状態を受け取る。<see cref="LoadRoot"/> の<b>前</b>に呼ぶ
    /// （投入より後に渡すと取りこぼすため）。保存値の無いワークスペースでも空で呼び、前の保留を捨てる。</summary>
    public void SetPendingViewState(IEnumerable<string>? expandedPaths, string? selectedPath)
    {
        _pendingExpandedPaths = new HashSet<string>(
            (expandedPaths ?? []).Where(p => !string.IsNullOrWhiteSpace(p)),
            StringComparer.OrdinalIgnoreCase);
        _pendingSelectedPath = string.IsNullOrWhiteSpace(selectedPath) ? null : selectedPath;
    }

    /// <summary>スナップショット保存用：展開中のフォルダー（フルパス）。畳まれた枝の中は見えていないので
    /// 数えない。フォルダー見出し（複数フォルダー時）は常に自動展開されるので含めない。
    /// まだ適用されていない保留分も残す——投入前に保存が走っても状態を失わないように。</summary>
    public IReadOnlyList<string> CaptureExpandedPaths()
    {
        var result = new List<string>();
        CollectExpanded(Nodes, result);
        foreach (var pending in _pendingExpandedPaths)
            if (!result.Contains(pending, StringComparer.OrdinalIgnoreCase))
                result.Add(pending);
        return result.Take(MaxSavedExpandedPaths).ToList();
    }

    /// <summary>スナップショット保存用：選択中の項目（フルパス）。未投入なら保留中の値。</summary>
    public string? CaptureSelectedPath()
        => Walk(Nodes).FirstOrDefault(n => n.IsSelected && n.FullPath.Length > 0)?.FullPath
           ?? _pendingSelectedPath;

    private static void CollectExpanded(IEnumerable<FileNodeViewModel> level, List<string> result)
    {
        foreach (var node in level)
        {
            if (!node.IsDirectory || !node.IsExpanded)
                continue;
            if (!node.IsWorkspaceFolderRoot)
                result.Add(node.FullPath);
            CollectExpanded(node.Children, result);
        }
    }

    /// <summary>投入直後の階層へ保留中の展開・選択を適用し、scopes のいずれかの配下にある保留を消費する。
    /// scopes が空（Shell 名前空間表示など、パスで切り分けられない場合）なら保留をすべて消費する。
    /// 投入した階層に現れ得るパスは必ず scopes 配下にすること——取り残すと監視更新のたびに再適用され、
    /// 利用者が畳んだ枝が開き直る。展開は遅延読込を同期で走らせるので、開いた子もその場で続けて辿れる。</summary>
    private void ApplyPendingViewState(IEnumerable<FileNodeViewModel> level, params string?[] scopes)
    {
        if (_pendingExpandedPaths.Count == 0 && _pendingSelectedPath is null)
            return;

        FileNodeViewModel? selected = null;
        ApplyPendingLevel(level, ref selected);

        var roots = scopes.OfType<string>().ToList();
        if (roots.Count == 0)
        {
            _pendingExpandedPaths.Clear();
            _pendingSelectedPath = null;
        }
        else
            RemovePendingWhere(p => roots.Any(root => IsPathWithinSafe(p, root)));

        if (selected is not null)
        {
            SelectionRestored?.Invoke(this, selected);
            selected.IsSelected = true;
        }
    }

    private void ApplyPendingLevel(IEnumerable<FileNodeViewModel> level, ref FileNodeViewModel? selected)
    {
        // 展開で Children が差し替わるので、列挙中の変更を避けて写しを辿る。
        foreach (var node in level.ToList())
        {
            if (node.FullPath.Length == 0)
                continue;   // 遅延読込用ダミー
            if (_pendingSelectedPath is not null
                && string.Equals(node.FullPath, _pendingSelectedPath, StringComparison.OrdinalIgnoreCase))
                selected = node;
            if (!node.IsDirectory)
                continue;
            if (_pendingExpandedPaths.Contains(node.FullPath))
                node.IsExpanded = true;
            if (node.IsExpanded)
                ApplyPendingLevel(node.Children, ref selected);
        }
    }

    /// <summary>今後投入され得ない保留（復元時に存在しなかった追加フォルダー等のぶん）を捨てる。
    /// 残すと保存のたびに書き戻され続け、同じフォルダーを後で追加し直したときに古い状態が再適用される。
    /// ワークスペースフォルダーの確定（<see cref="RestoreAdditionalFolders"/>）直後に呼ぶ。</summary>
    private void PrunePendingViewStateToLoadableRoots()
    {
        if (_pendingExpandedPaths.Count == 0 && _pendingSelectedPath is null)
            return;

        var roots = (_multiRootStates.Count > 0
                ? _multiRootStates.Values.SelectMany(s => new[] { s.FolderPath, s.DisplayedPath }.OfType<string>())
                : new[] { _workspaceRoot, _currentRoot }.OfType<string>())
            .ToList();
        RemovePendingWhere(p => !roots.Any(root => IsPathWithinSafe(p, root)));
    }

    private void RemovePendingWhere(Func<string, bool> predicate)
    {
        _pendingExpandedPaths.RemoveWhere(p => predicate(p));
        if (_pendingSelectedPath is not null && predicate(_pendingSelectedPath))
            _pendingSelectedPath = null;
    }

    private static bool IsPathWithinSafe(string path, string directory)
    {
        try { return IsPathWithin(path, directory); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
    }
}
