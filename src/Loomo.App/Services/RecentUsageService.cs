using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>最近利用したファイルと頻繁に利用するフォルダーの収集・表示用状態。
/// 保存単位は現在の <see cref="WorkspaceSnapshot"/> で、ファイル内容・検索語・AI入力は扱わない。</summary>
public sealed class RecentUsageService
{
    public const int MaxRecentFiles = 20;
    public const int MaxFrequentFolders = 12;

    public RecentUsageState Load(WorkspaceSnapshot? workspace)
    {
        if (workspace is null)
            return RecentUsageState.Empty;

        var files = Clean(workspace, workspace.RecentFiles ?? [], isDirectory: false)
                .GroupBy(Key, StringComparer.OrdinalIgnoreCase)
                .Select(Merge)
                .OrderByDescending(x => x.LastUsedUtc)
                .ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Take(MaxRecentFiles)
                .ToList();
        var folders = Clean(workspace, workspace.FrequentFolders ?? [], isDirectory: true)
                .GroupBy(Key, StringComparer.OrdinalIgnoreCase)
                .Select(Merge)
                .OrderByDescending(x => x.UseCount)
                .ThenByDescending(x => x.LastUsedUtc)
                .ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Take(MaxFrequentFolders)
                .ToList();
        workspace.RecentFiles = files;
        workspace.FrequentFolders = folders;
        return new RecentUsageState(files, folders);
    }

    public bool RecordFile(WorkspaceSnapshot workspace, string? fullPath, DateTime? nowUtc = null)
    {
        workspace.RecentFiles ??= new();
        NormalizeInPlace(workspace, workspace.RecentFiles, isDirectory: false);
        return Record(workspace, workspace.RecentFiles, fullPath, isDirectory: false, MaxRecentFiles, nowUtc);
    }

    public bool RecordFolder(WorkspaceSnapshot workspace, string? fullPath, DateTime? nowUtc = null)
    {
        workspace.FrequentFolders ??= new();
        NormalizeInPlace(workspace, workspace.FrequentFolders, isDirectory: true);
        return Record(workspace, workspace.FrequentFolders, fullPath, isDirectory: true, MaxFrequentFolders, nowUtc);
    }

    /// <summary>履歴の存在確認・権限確認をUIスレッドから外すための入口。</summary>
    public Task<bool> RecordFileAsync(WorkspaceSnapshot workspace, string? fullPath,
        DateTime? nowUtc = null, CancellationToken cancellationToken = default)
        => Task.Run(() => RecordFile(workspace, fullPath, nowUtc), cancellationToken);

    /// <summary>履歴の存在確認・権限確認をUIスレッドから外すための入口。</summary>
    public Task<bool> RecordFolderAsync(WorkspaceSnapshot workspace, string? fullPath,
        DateTime? nowUtc = null, CancellationToken cancellationToken = default)
        => Task.Run(() => RecordFolder(workspace, fullPath, nowUtc), cancellationToken);

    public Task<RecentUsageState> LoadAsync(WorkspaceSnapshot? workspace,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Load(workspace), cancellationToken);

    public static string Resolve(WorkspaceSnapshot workspace, RecentPathSnapshot item)
    {
        var root = RootAt(workspace, item.RootIndex);
        if (root is null || string.IsNullOrWhiteSpace(item.RelativePath))
            return root ?? "";
        try
        {
            if (Path.IsPathRooted(item.RelativePath)) return "";
            var full = Path.GetFullPath(Path.Combine(root, item.RelativePath));
            return IsWithin(root, full) ? full : "";
        }
        catch { return ""; }
    }

    public static string DisplayName(string fullPath, bool isDirectory)
    {
        try
        {
            var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!isDirectory) return Path.GetFileName(trimmed);
            var name = new DirectoryInfo(trimmed).Name;
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }
        catch { return fullPath; }
    }

    public static string RelativeLabel(WorkspaceSnapshot workspace, RecentPathSnapshot item)
    {
        var root = RootAt(workspace, item.RootIndex);
        if (root is null) return item.RelativePath;
        return item.RelativePath.Length == 0 ? DisplayName(root, isDirectory: true) : item.RelativePath;
    }

    private static bool Record(
        WorkspaceSnapshot workspace,
        List<RecentPathSnapshot> entries,
        string? fullPath,
        bool isDirectory,
        int max,
        DateTime? nowUtc)
    {
        if (!TryLocate(workspace, fullPath, isDirectory, out var rootIndex, out var relative))
            return false;

        var existing = entries.FirstOrDefault(x => x.RootIndex == rootIndex
            && string.Equals(x.RelativePath, relative, StringComparison.OrdinalIgnoreCase));
        var now = nowUtc ?? DateTime.UtcNow;
        if (existing is null)
        {
            entries.Add(new RecentPathSnapshot
            {
                RootIndex = rootIndex,
                RelativePath = relative,
                LastUsedUtc = now,
                UseCount = 1,
            });
        }
        else
        {
            existing.LastUsedUtc = now;
            existing.UseCount = existing.UseCount >= int.MaxValue
                ? int.MaxValue
                : Math.Max(1, existing.UseCount) + 1;
        }

        var kept = entries
            .Where(x => TryResolveExisting(workspace, x, isDirectory))
            .OrderByDescending(x => isDirectory ? x.UseCount : 0)
            .ThenByDescending(x => x.LastUsedUtc)
            .Take(max)
            .ToList();
        entries.Clear();
        entries.AddRange(kept);
        return true;
    }

    private static IEnumerable<RecentPathSnapshot> Clean(
        WorkspaceSnapshot workspace, IEnumerable<RecentPathSnapshot> source, bool isDirectory)
        => source.Where(item => TryResolveExisting(workspace, item, isDirectory));

    private static RecentPathSnapshot Merge(IEnumerable<RecentPathSnapshot> group)
    {
        var items = group.ToList();
        var latest = items.OrderByDescending(x => x.LastUsedUtc).First();
        var count = items.Aggregate(0, (total, item) =>
            total > int.MaxValue - Math.Max(1, item.UseCount)
                ? int.MaxValue
                : total + Math.Max(1, item.UseCount));
        return new RecentPathSnapshot
        {
            RootIndex = latest.RootIndex,
            RelativePath = latest.RelativePath,
            LastUsedUtc = latest.LastUsedUtc,
            UseCount = count,
        };
    }

    private static void NormalizeInPlace(WorkspaceSnapshot workspace,
        List<RecentPathSnapshot> entries, bool isDirectory)
    {
        var normalized = Clean(workspace, entries, isDirectory)
            .GroupBy(Key, StringComparer.OrdinalIgnoreCase)
            .Select(Merge)
            .ToList();
        entries.Clear();
        entries.AddRange(normalized);
    }

    private static string Key(RecentPathSnapshot item)
        => $"{item.RootIndex}\0{item.RelativePath}";

    private static bool TryResolveExisting(WorkspaceSnapshot workspace, RecentPathSnapshot item, bool isDirectory)
    {
        var path = Resolve(workspace, item);
        if (path.Length == 0) return false;
        try
        {
            if (isDirectory)
            {
                if (!Directory.Exists(path)) return false;
                // Exists() はアクセス拒否を隠すことがある。列挙を一度試して、移動時の失敗を表示へ残さない。
                using var e = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                _ = e.MoveNext();
                return true;
            }
            if (!File.Exists(path)) return false;
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return false; }
    }

    private static bool TryLocate(WorkspaceSnapshot workspace, string? raw, bool isDirectory,
        out int rootIndex, out string relative)
    {
        rootIndex = -1;
        relative = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string full;
        try { full = Path.GetFullPath(raw); }
        catch { return false; }

        if (isDirectory ? !TryDirectory(full) : !TryFile(full)) return false;
        var roots = WorkspaceRoots(workspace);
        var owner = roots
            .Select((root, index) => (root, index))
            .Where(x => IsWithin(x.root, full))
            .OrderByDescending(x => x.root!.Length)
            .FirstOrDefault();
        if (owner.root is null) return false;

        try
        {
            var rel = Path.GetRelativePath(owner.root, full);
            if (rel == ".") rel = "";
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return false;
            rootIndex = owner.index;
            relative = rel;
            return true;
        }
        catch { return false; }
    }

    private static bool TryFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.GetAttributes(path);
            return true;
        }
        catch { return false; }
    }

    private static bool TryDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return false;
            File.GetAttributes(path);
            return true;
        }
        catch { return false; }
    }

    private static IReadOnlyList<string?> WorkspaceRoots(WorkspaceSnapshot workspace)
    {
        var roots = new List<string?>();
        roots.Add(TryFullPath(workspace.RootPath));
        roots.AddRange((workspace.AdditionalFolders ?? [])
            .Select(x => TryFullPath(x?.FolderPath)));
        return roots;
    }

    private static string? RootAt(WorkspaceSnapshot workspace, int index)
    {
        var roots = WorkspaceRoots(workspace);
        return index >= 0 && index < roots.Count ? roots[index] : null;
    }

    private static string? TryFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }

    private static bool IsWithin(string? root, string path)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            path = Path.GetFullPath(path);
            return string.Equals(root, path, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

public sealed record RecentUsageState(
    IReadOnlyList<RecentPathSnapshot> Files,
    IReadOnlyList<RecentPathSnapshot> Folders)
{
    public static RecentUsageState Empty { get; } = new([], []);
}

public sealed record RecentUsageItem(
    string FullPath,
    string Name,
    string Location,
    DateTime LastUsedUtc,
    int UseCount,
    bool IsDirectory);

/// <summary>RecentUsageService を WPF の一覧へ投影する VM。ワークスペース切替時に差し替える。</summary>
public sealed class RecentItemsViewModel : ObservableObject
{
    private readonly RecentUsageService _service;
    private readonly Dispatcher? _dispatcher;
    private readonly object _queueGate = new();
    private CancellationTokenSource _lifetime = new();
    private Task _pending = Task.CompletedTask;
    private WorkspaceSnapshot? _workspace;
    private int _workspaceGeneration;

    public RecentItemsViewModel(RecentUsageService service)
    {
        _service = service;
        _dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
    }

    public ObservableCollection<RecentUsageItem> RecentFiles { get; } = new();
    public ObservableCollection<RecentUsageItem> FrequentFolders { get; } = new();
    public bool HasItems => RecentFiles.Count > 0 || FrequentFolders.Count > 0;

    public event EventHandler<RecentUsageItem>? NavigationRequested;
    public event EventHandler? Changed;

    public void SetWorkspace(WorkspaceSnapshot? workspace)
    {
        CancellationTokenSource previous;
        Task prior;
        lock (_queueGate)
        {
            previous = _lifetime;
            _lifetime = new CancellationTokenSource();
            _workspace = workspace;
            _workspaceGeneration++;
            prior = _pending;
            _pending = LoadAfterAsync(prior, workspace, _workspaceGeneration, _lifetime.Token);
        }
        previous.Cancel();
        ApplyState(RecentUsageState.Empty);
    }

    public void RecordFile(string? path) => QueueRecord(path, isDirectory: false);
    public void RecordFolder(string? path) => QueueRecord(path, isDirectory: true);

    public void Navigate(RecentUsageItem? item)
    {
        if (item is null || _workspace is null) return;
        var valid = item.IsDirectory ? Directory.Exists(item.FullPath) : File.Exists(item.FullPath);
        if (!valid) { QueueRefresh(); return; }
        NavigationRequested?.Invoke(this, item);
    }

    private void QueueRecord(string? path, bool isDirectory)
    {
        lock (_queueGate)
        {
            if (_workspace is null) return;
            var prior = _pending;
            _pending = RecordAfterAsync(prior, _workspace, _workspaceGeneration, path, isDirectory,
                _lifetime.Token);
        }
    }

    private void QueueRefresh()
    {
        lock (_queueGate)
        {
            var prior = _pending;
            _pending = LoadAfterAsync(prior, _workspace, _workspaceGeneration, _lifetime.Token);
        }
    }

    private async Task LoadAfterAsync(Task prior, WorkspaceSnapshot? workspace,
        int generation, CancellationToken cancellationToken)
    {
        await IgnoreFailureAsync(prior).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested) return;
        try
        {
            var state = await _service.LoadAsync(workspace, cancellationToken).ConfigureAwait(false);
            PostToUi(() =>
            {
                if (IsCurrent(workspace, generation)) ApplyState(state);
            });
        }
        catch (OperationCanceledException) { }
    }

    private async Task RecordAfterAsync(Task prior, WorkspaceSnapshot workspace,
        int generation, string? path, bool isDirectory, CancellationToken cancellationToken)
    {
        await IgnoreFailureAsync(prior).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested) return;
        try
        {
            var changed = isDirectory
                ? await _service.RecordFolderAsync(workspace, path, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await _service.RecordFileAsync(workspace, path, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!changed || cancellationToken.IsCancellationRequested) return;
            var state = await _service.LoadAsync(workspace, cancellationToken).ConfigureAwait(false);
            PostToUi(() =>
            {
                if (!IsCurrent(workspace, generation)) return;
                ApplyState(state);
                Changed?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (OperationCanceledException) { }
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { /* 履歴更新はUI操作を失敗させない */ }
    }

    private bool IsCurrent(WorkspaceSnapshot? workspace, int generation)
        => ReferenceEquals(_workspace, workspace) && _workspaceGeneration == generation;

    private void PostToUi(Action action)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess())
            action();
        else
            _dispatcher.BeginInvoke(action);
    }

    private void ApplyState(RecentUsageState state)
    {
        RecentFiles.Clear();
        FrequentFolders.Clear();
        if (_workspace is not null)
        {
            foreach (var item in state.Files)
                Add(RecentFiles, item, false);
            foreach (var item in state.Folders)
                Add(FrequentFolders, item, true);
        }
        OnPropertyChanged(nameof(HasItems));
    }

    private void Add(ObservableCollection<RecentUsageItem> target, RecentPathSnapshot item, bool isDirectory)
    {
        if (_workspace is null) return;
        var full = RecentUsageService.Resolve(_workspace, item);
        if (full.Length == 0) return;
        target.Add(new RecentUsageItem(full, RecentUsageService.DisplayName(full, isDirectory),
            RecentUsageService.RelativeLabel(_workspace, item), item.LastUsedUtc, item.UseCount, isDirectory));
    }
}
