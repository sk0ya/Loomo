using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Services;

/// <summary>ワークツリーへの切替要求（§24.17.1）。</summary>
/// <param name="Target">行き先のワークツリー。</param>
/// <param name="From">今の部屋が Git 操作の対象にしているワークツリー（写し元の基準。マルチルートでは
/// 主フォルダーとは限らない）。</param>
/// <param name="Main">本体のワークツリー（一覧の畳み先）。</param>
/// <param name="Worktrees">このリポジトリのワークツリー全部（パスの持ち主を決めるのに使う）。</param>
public sealed record WorktreeSwitchRequest(
    string Target, string From, string Main, IReadOnlyList<string> Worktrees);

/// <summary>
/// ワークツリーの部屋を「今の部屋を写して」作る（§24.17.1）。ブランチを切り替えたときのように、
/// 配置・タブ・キャレット・ツリーの開き具合をそのまま持ち越し、ファイルだけをワークツリー側へ付け替える。
/// ワークツリーどうしは相対パスの構成が同じなので、ほとんどのタブはそのまま開ける。
///
/// <para><b>付け替えるのは「写し元のワークツリーのもの」だけ</b>。パスの持ち主は、そのパスを含むワークツリーの
/// うち<b>いちばん深いもの</b>で決める——ワークツリーはリポジトリの中（<c>.claude/worktrees/x</c>）に置かれる
/// ことが多く、本体のフォルダーの下には兄弟のワークツリーも入れ子で並んでいる。「本体の下なら付け替える」
/// だけだと、兄弟のワークツリーの中のパスを <c>A/.claude/worktrees/B/…</c> と化けさせ、ワークツリーの部屋から
/// 本体へ写すときは（どのパスも本体の下なので）何も付け替えない。</para>
///
/// <para>写さないもの：<b>未保存の編集</b>（元の部屋のファイルへの編集で、ワークツリーのファイルへ
/// 持ち込むと別物を上書きする）、切り離しウィンドウ、ペグボード、残した検索結果、コンポーザーの本文、
/// Git の比較基準——どれも「元の部屋で今やっていること」で、部屋の形ではない。付け替えた先に無いもの
/// （ワークツリーに無いファイル・ignore されていて生えていない <c>bin</c> 等）も持ち込まない。</para>
///
/// <para>ターミナルとブラウザのタブは<b>新しい ID</b> にする。ターミナルのタブ ID は常駐ホストのセッションの鍵
/// （§34）なので、同じ ID を写すと元の部屋のシェルを奪い合う。</para>
/// </summary>
public static class WorktreeRoomSeed
{
    /// <param name="source">写す元（今の部屋。直前に捕まえた最新の状態）。変更しない。</param>
    /// <param name="exists">付け替えた先にファイル／フォルダーがあるか。</param>
    public static WorkspaceSnapshot Seed(
        WorkspaceSnapshot source, WorktreeSwitchRequest request, Func<string, bool> exists)
    {
        var newRoot = Trim(Path.GetFullPath(request.Target));
        var map = new PathMap(Trim(Path.GetFullPath(request.From)), newRoot, request.Worktrees);
        var seed = Clone(source);

        seed.Id = Guid.NewGuid();
        seed.IsDetailsLoaded = true;
        seed.RootPath = newRoot;
        seed.Name = WorkspaceListViewModel.DisplayName(newRoot);
        seed.CustomName = null;
        seed.Pinned = false;
        seed.LastUsedUtc = DateTime.UtcNow;
        seed.WorktreeOf = null;   // 畳み先は呼び出し側が決める（本体の部屋かどうかは一覧が知っている）

        // ===== フォルダー：写し元のワークツリーを抱えていたフォルダーが、ワークツリーに置き換わる =====
        // マルチルートでは Git の対象が追加フォルダー側のこともある。主フォルダーを決め打ちせず、
        // ワークツリーと重なるフォルダーを探してそれを差し替え、残りは追加フォルダーとして持ち越す。
        var oldFolders = new List<WorkspaceFolderPin>
        {
            new() { FolderPath = source.RootPath, PinnedFolders = seed.PinnedFolders, TreeRootPath = seed.TreeRootPath },
        };
        oldFolders.AddRange(seed.AdditionalFolders);
        var replaced = oldFolders.FirstOrDefault(f => Overlaps(f.FolderPath, map.From));
        seed.PinnedFolders = replaced is null ? new()
            : replaced.PinnedFolders.Select(map.Rebase)
                .Where(p => WorkspacePaths.IsWithin(newRoot, p) && exists(p)).ToList();
        seed.TreeRootPath = replaced?.TreeRootPath is { } treeRoot
            && map.Rebase(treeRoot) is var rebasedRoot && WorkspacePaths.IsWithin(newRoot, rebasedRoot)
            && exists(rebasedRoot) ? rebasedRoot : null;
        // 追加フォルダーはワークツリーと親子関係になれない（AddFolder の不変条件）。写し元のリポジトリも外す。
        seed.AdditionalFolders = oldFolders
            .Where(f => !ReferenceEquals(f, replaced)
                        && !Overlaps(f.FolderPath, newRoot) && !Overlaps(f.FolderPath, map.From))
            .ToList();
        var newFolders = new List<string> { newRoot };
        newFolders.AddRange(seed.AdditionalFolders.Select(f => f.FolderPath));

        // 最近の項目はルート番号＋相対パス。フォルダーの並びが変わるので、絶対パスへ戻して振り直す。
        seed.RecentFiles = RemapRecent(seed.RecentFiles, oldFolders, newFolders, map);
        seed.FrequentFolders = RemapRecent(seed.FrequentFolders, oldFolders, newFolders, map);

        // ===== エディタ：ファイルだけ付け替え、本文は持ち込まない =====
        seed.Editor = new EditorSnapshot();
        seed.EditorTabs = seed.EditorTabs
            .Where(t => !string.IsNullOrWhiteSpace(t.FilePath))
            .Select(t =>
            {
                t.FilePath = map.Rebase(t.FilePath!);
                t.Title = Path.GetFileName(t.FilePath);
                t.Text = null;
                t.DeferredTextPath = null;
                t.IsModified = false;
                return t;
            })
            .Where(t => exists(t.FilePath!))
            .ToList();
        // EditorViewLayout は同じタブ ID を指したまま写す（落としたタブの葉は復元側が読み飛ばす）。

        // ===== ターミナル：新しい ID・cwd を付け替え（無ければワークツリーの根）、分割木の葉も読み替える =====
        var terminalIds = seed.TerminalTabs.ToDictionary(t => t.Id, _ => Guid.NewGuid());
        foreach (var tab in seed.TerminalTabs)
        {
            tab.Id = terminalIds[tab.Id];
            tab.WorkingDirectory = tab.WorkingDirectory is { } cwd && map.Rebase(cwd) is var moved && exists(moved)
                ? moved : newRoot;
        }
        seed.ActiveTerminalTabId = seed.ActiveTerminalTabId is { } activeTerminal
            && terminalIds.TryGetValue(activeTerminal, out var mapped) ? mapped : null;
        RemapTabIds(seed.TerminalViewLayout, terminalIds);

        // ===== ブラウザ：中身（URL）は同じで、タブの実体は別 =====
        foreach (var tab in seed.BrowserTabs)
            tab.Id = Guid.NewGuid();

        // ===== ツリー・ファイル一覧 =====
        seed.TreeExpandedPaths = seed.TreeExpandedPaths.Select(map.Rebase).Where(exists).ToList();
        seed.TreeSelectedPath = seed.TreeSelectedPath is { } selected && map.Rebase(selected) is var movedSelection
            && exists(movedSelection) ? movedSelection : null;
        if (seed.Files is { } files)
        {
            foreach (var column in files.Migrate().Columns)
            {
                column.CurrentFolder = column.CurrentFolder is { } folder && map.Rebase(folder) is var movedFolder
                    && exists(movedFolder) ? movedFolder : null;
                column.FolderColumnSettings = column.FolderColumnSettings
                    .GroupBy(kv => map.Rebase(kv.Key), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
            }
            files.CurrentFolder = null;   // 旧形式の欄は Migrate で Columns へ移した
        }

        // ===== 元の部屋で今やっていること（写さない） =====
        seed.DetachedWindows = new();
        seed.Pegboard = new();
        seed.SearchTabs = new();
        seed.ComposerText = null;
        seed.GitCompare = null;

        return seed;
    }

    /// <summary>パスの持ち主（それを含むいちばん深いワークツリー）が写し元なら、行き先の同じ相対位置へ。
    /// それ以外（兄弟のワークツリー・別フォルダー・ワークスペース外）はそのまま。</summary>
    internal sealed class PathMap
    {
        private readonly string[] _worktrees;

        public PathMap(string from, string to, IEnumerable<string> worktrees)
        {
            From = from;
            To = to;
            _worktrees = worktrees.Select(w => Trim(Path.GetFullPath(w)))
                .Append(from).Append(to)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public string From { get; }
        public string To { get; }

        public string Rebase(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;
            var owner = _worktrees.Where(w => WorkspacePaths.IsWithin(w, path)).MaxBy(w => w.Length);
            if (owner is null || !string.Equals(owner, From, StringComparison.OrdinalIgnoreCase))
                return path;
            var relative = Path.GetRelativePath(From, path);
            return relative == "." ? To : Path.Combine(To, relative);
        }
    }

    private static List<RecentPathSnapshot> RemapRecent(
        IEnumerable<RecentPathSnapshot> items, IReadOnlyList<WorkspaceFolderPin> oldFolders,
        IReadOnlyList<string> newFolders, PathMap map)
    {
        var result = new List<RecentPathSnapshot>();
        foreach (var item in items)
        {
            if (item.RootIndex < 0 || item.RootIndex >= oldFolders.Count)
                continue;
            var full = map.Rebase(Path.GetFullPath(Path.Combine(oldFolders[item.RootIndex].FolderPath, item.RelativePath)));
            var index = -1;
            for (var i = 0; i < newFolders.Count && index < 0; i++)
                if (WorkspacePaths.IsWithin(newFolders[i], full))
                    index = i;
            if (index < 0)
                continue;
            item.RootIndex = index;
            item.RelativePath = Path.GetRelativePath(newFolders[index], full);
            result.Add(item);
        }
        return result;
    }

    private static bool Overlaps(string a, string b)
        => WorkspacePaths.IsWithin(a, b) || WorkspacePaths.IsWithin(b, a);

    private static string Trim(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static void RemapTabIds(ViewportNodeSnapshot? node, IReadOnlyDictionary<Guid, Guid> ids)
    {
        if (node is null)
            return;
        if (node.TabId is { } id)
            node.TabId = ids.TryGetValue(id, out var mapped) ? mapped : null;
        foreach (var child in node.Children)
            RemapTabIds(child, ids);
    }

    /// <summary>保存と同じ直列化で深く複製する（入れ子の可変リストを元の部屋と共有しないため）。</summary>
    private static WorkspaceSnapshot Clone(WorkspaceSnapshot source)
    {
        var json = JsonSerializer.Serialize(
            new WorkspaceState { Workspaces = [source] }, WorkspaceStateJsonContext.Default.WorkspaceState);
        return JsonSerializer.Deserialize(json, WorkspaceStateJsonContext.Default.WorkspaceState)!
            .Workspaces[0];
    }
}
