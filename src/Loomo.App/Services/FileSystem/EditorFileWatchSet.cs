using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 開いているエディタタブのファイルを1つずつ見張る監視の集まり（§24.57）。中身は
/// <see cref="SingleFileWatcher"/> を<b>ファイルごとに1つ</b>——再帰監視はしない（理由は
/// <see cref="SingleFileWatcher"/> のコメント）。同じファイルを2枚のタブ（分割・プレビュー）で開いていても監視は1つ。
/// <para>
/// 張り替えは <see cref="Sync"/> 一本で、「いま見張るべきパスの集合」を渡すと差分だけ張る／外す。
/// 同じ対象の監視は作り直さない——作り直す瞬間の変更を取りこぼすので。
/// </para>
/// </summary>
internal sealed class EditorFileWatchSet : IDisposable
{
    private readonly Func<SingleFileWatcher> _create;
    private readonly Dictionary<string, SingleFileWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <param name="changed">見張っているファイルが変わったときに UI スレッドで呼ばれる（デバウンス済み・引数はフルパス）。</param>
    /// <param name="create">監視の作り方。テストは同期に通知するものを渡す。</param>
    public EditorFileWatchSet(Action<string> changed, Func<Action<string>, SingleFileWatcher>? create = null)
    {
        var factory = create ?? (callback => new SingleFileWatcher(callback));
        _create = () => factory(changed);
    }

    /// <summary>いま見張っているファイル（フルパス）。</summary>
    public IReadOnlyCollection<string> Targets => _watchers.Keys;

    /// <summary>見張りをこの集合に合わせる。null・空・解決できないパスは無視する。</summary>
    public void Sync(IEnumerable<string?> paths)
    {
        if (_disposed)
            return;
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
            if (ToFullPath(path) is { } full)
                wanted.Add(full);

        foreach (var stale in _watchers.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            _watchers[stale].Dispose();
            _watchers.Remove(stale);
        }
        foreach (var path in wanted)
        {
            if (_watchers.ContainsKey(path))
                continue;
            var watcher = _create();
            watcher.Watch(path);
            _watchers[path] = watcher;
        }
    }

    private static string? ToFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var watcher in _watchers.Values)
            watcher.Dispose();
        _watchers.Clear();
    }
}
