using System;
using System.Collections.Generic;
using System.Linq;

namespace sk0ya.Loomo.App.Services;

/// <summary>手で閉じたターミナルのタブ。シェルは常駐ホストが <see cref="ClosedTerminalTabs.KeepFor"/> のあいだ預かっている（§24.41）。</summary>
public sealed record ClosedTerminalTab(Guid Id, Guid WorkspaceId, string? CustomName, string? WorkingDirectory, DateTime ClosedUtc,
    string? ColorKey = null);

/// <summary>
/// 閉じたターミナルを開き直すための控え（§24.41・Ghostty 1.2.0 の「undo terminal closure」）。新しいものから戻す。
/// 期限（ホストが預かる時間）を過ぎたものは、シェルがもう居ないので戻さない。保存はしない——預かるのは
/// 常駐ホストの中だけで、Loomo を終えればホストごと終わる。
/// </summary>
public sealed class ClosedTerminalTabs
{
    /// <summary>ホストに預ける時間。うっかり閉じたと気づくには足り、裏で動き続けて邪魔にならない長さ。</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(3);

    /// <summary>覚えておく数（これより古いものはホストの期限で消える）。</summary>
    public const int MaxEntries = 10;

    private readonly List<ClosedTerminalTab> _entries = new();

    public void Push(ClosedTerminalTab tab)
    {
        _entries.RemoveAll(e => e.Id == tab.Id);
        _entries.Add(tab);
        if (_entries.Count > MaxEntries)
            _entries.RemoveAt(0);
    }

    /// <summary>このワークスペースで最後に閉じた、まだ期限内のタブを取り出す。無ければ null。</summary>
    public ClosedTerminalTab? TakeLatest(Guid workspaceId, DateTime nowUtc)
    {
        Prune(nowUtc);
        var latest = _entries.LastOrDefault(e => e.WorkspaceId == workspaceId);
        if (latest is not null)
            _entries.Remove(latest);
        return latest;
    }

    /// <summary>このワークスペースで開き直せる数。</summary>
    public int Count(Guid workspaceId, DateTime nowUtc)
    {
        Prune(nowUtc);
        return _entries.Count(e => e.WorkspaceId == workspaceId);
    }

    private void Prune(DateTime nowUtc) => _entries.RemoveAll(e => nowUtc - e.ClosedUtc >= KeepFor);
}
