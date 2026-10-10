using System;
using System.Collections.Generic;
using System.Linq;

namespace sk0ya.Loomo.App.Services;

/// <summary>エディタ上の1地点（ファイル＋0始まりの行・列）。</summary>
public readonly record struct EditorLocation(string Path, int Line, int Column)
{
    public bool IsSameFile(EditorLocation other)
        => string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>同じファイルで、行の差が <paramref name="lines"/> 未満か（＝「同じ場所」とみなす近さ）。</summary>
    public bool IsNear(EditorLocation other, int lines)
        => IsSameFile(other) && Math.Abs(Line - other.Line) < lines;
}

/// <summary>
/// エディタの「戻る／進む」——ファイルをまたいだ<b>場所</b>の履歴（Rider の Navigate Back/Forward 相当）。
/// 純ロジック（WPF 非依存・テスト対象）。
/// <para>
/// 記録はキャレットの動き（<see cref="Record"/>）だけから行う。定義ジャンプ・問題一覧・パレット・検索・
/// タブ切替……と呼び出し元は数十あるので、それぞれに「ここで履歴を積む」を書くと必ず漏れる。代わりに
/// 「近くで動いたら今の項目を更新、遠くへ飛んだら新しい項目」という形だけで判定する（VS Code と同じ考え方）。
/// </para>
/// <list type="bullet">
/// <item>同じファイルで <see cref="JumpThresholdLines"/> 行未満の移動 → 今の項目をその場で更新（打鍵・矢印移動は積まない）。</item>
/// <item>別ファイル、または閾値以上の移動 → 新しい項目を積み、進む側を捨てる。</item>
/// <item>直前に積んだ（置き換えた）項目と<b>同じファイル</b>で <see cref="CoalesceWindow"/> 以内の大移動 → 積まずに置き換える。
/// ファイルを開く経路は「タブが前回のキャレットで出る→目的の行へ飛ぶ」の二段で動くので、置き換えないと
/// 通り道が1件余計に残る。ページ送りの連打も同じ理由で1件にまとまる。</item>
/// </list>
/// <para>
/// 戻る／進むで飛んでいる間のキャレットの動きは <see cref="BeginNavigation"/>〜<see cref="EndNavigation"/> で
/// 無視する。ファイルを開く途中に出る「前回のキャレット位置」を新しい移動と取り違えると、進む側が消える。
/// </para>
/// </summary>
public sealed class EditorLocationHistory
{
    /// <summary>この行数以上離れたら「別の場所へ飛んだ」とみなす。</summary>
    public const int JumpThresholdLines = 10;

    /// <summary>同じファイル内の大移動をまとめる時間。</summary>
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(800);

    private readonly List<EditorLocation> _entries = new();
    private readonly int _capacity;
    private int _index = -1;
    private DateTime _lastPushUtc = DateTime.MinValue;
    private int _navigationDepth;

    public EditorLocationHistory(int capacity = 100)
    {
        _capacity = Math.Max(2, capacity);
    }

    public bool CanGoBack => _index > 0;
    public bool CanGoForward => _index >= 0 && _index < _entries.Count - 1;
    public bool IsNavigating => _navigationDepth > 0;
    public EditorLocation? Current => _index >= 0 ? _entries[_index] : null;

    /// <summary>古い順の全項目（最近の場所の一覧用）。</summary>
    public IReadOnlyList<EditorLocation> Entries => _entries;

    /// <summary>キャレットが <paramref name="location"/> へ動いた。</summary>
    public void Record(EditorLocation location, DateTime nowUtc)
    {
        if (IsNavigating || string.IsNullOrEmpty(location.Path))
            return;
        if (_index < 0)
        {
            Push(location, nowUtc);
            return;
        }

        var current = _entries[_index];
        if (location.IsNear(current, JumpThresholdLines))
        {
            _entries[_index] = location;
            return;
        }
        if (location.IsSameFile(current) && nowUtc - _lastPushUtc < CoalesceWindow && _index > 0)
        {
            // 時刻も進める：ページ送りを押し続けている間は、何度でも同じ1件にまとまる。
            _lastPushUtc = nowUtc;
            // 置き換え先が1つ前の項目と「同じ場所」になったら、二重に残さず1つ前へ畳む。
            if (location.IsNear(_entries[_index - 1], JumpThresholdLines))
            {
                TruncateForwardFrom(_index);
                _index--;
                _entries[_index] = location;
                return;
            }
            _entries[_index] = location;
            TruncateForwardFrom(_index + 1);
            return;
        }
        Push(location, nowUtc);
    }

    /// <summary>1つ戻った先。<paramref name="isAvailable"/> が false の項目（消えたファイル等）は捨てて飛ばす。</summary>
    public EditorLocation? GoBack(Func<EditorLocation, bool>? isAvailable = null)
    {
        while (_index > 0)
        {
            _index--;
            var target = _entries[_index];
            if (isAvailable is null || isAvailable(target))
                return target;
            _entries.RemoveAt(_index);
        }
        return null;
    }

    /// <summary>1つ進んだ先。</summary>
    public EditorLocation? GoForward(Func<EditorLocation, bool>? isAvailable = null)
    {
        while (_index < _entries.Count - 1)
        {
            var target = _entries[_index + 1];
            if (isAvailable is null || isAvailable(target))
            {
                _index++;
                return target;
            }
            _entries.RemoveAt(_index + 1);
        }
        return null;
    }

    /// <summary>戻る／進むで飛び始めた。この間のキャレットの動きは記録しない。</summary>
    public void BeginNavigation() => _navigationDepth++;

    public void EndNavigation()
    {
        if (_navigationDepth > 0)
            _navigationDepth--;
    }

    /// <summary>新しい順・近い場所は1件にまとめた一覧（Rider の Recent Locations）。今いる場所は除く。</summary>
    public IReadOnlyList<EditorLocation> RecentDistinct(int max = 50)
    {
        var result = new List<EditorLocation>();
        var current = Current;
        for (var i = _entries.Count - 1; i >= 0 && result.Count < max; i--)
        {
            var entry = _entries[i];
            if (current is { } now && entry.IsNear(now, JumpThresholdLines))
                continue;
            if (result.Any(seen => seen.IsNear(entry, JumpThresholdLines)))
                continue;
            result.Add(entry);
        }
        return result;
    }

    public void Clear()
    {
        _entries.Clear();
        _index = -1;
        _lastPushUtc = DateTime.MinValue;
    }

    private void Push(EditorLocation location, DateTime nowUtc)
    {
        TruncateForwardFrom(_index + 1);
        _entries.Add(location);
        _index = _entries.Count - 1;
        if (_entries.Count > _capacity)
        {
            _entries.RemoveAt(0);
            _index--;
        }
        _lastPushUtc = nowUtc;
    }

    private void TruncateForwardFrom(int start)
    {
        if (start < _entries.Count)
            _entries.RemoveRange(start, _entries.Count - start);
    }
}

/// <summary>
/// 編集した場所の履歴（Rider の Last Edit Location）。純ロジック。
/// 同じファイルの近く（<see cref="EditorLocationHistory.JumpThresholdLines"/> 行未満）での連続編集は1件に
/// まとめ、<see cref="Previous"/> を続けて呼ぶと古い編集箇所へ順に遡る。新しい編集が入ると遡りは先頭へ戻る。
/// </summary>
public sealed class EditLocationTrail
{
    private readonly List<EditorLocation> _entries = new();
    private readonly int _capacity;
    private int _cursor = -1;

    public EditLocationTrail(int capacity = 50)
    {
        _capacity = Math.Max(1, capacity);
    }

    public IReadOnlyList<EditorLocation> Entries => _entries;

    public void Record(EditorLocation location)
    {
        if (string.IsNullOrEmpty(location.Path))
            return;
        _cursor = -1;
        if (_entries.Count > 0 && location.IsNear(_entries[^1], EditorLocationHistory.JumpThresholdLines))
        {
            _entries[^1] = location;
            return;
        }
        _entries.RemoveAll(e => e.IsNear(location, EditorLocationHistory.JumpThresholdLines));
        _entries.Add(location);
        if (_entries.Count > _capacity)
            _entries.RemoveAt(0);
    }

    /// <summary>次に遡る編集箇所。初回は「今いる場所ではない最新の編集」、続けて呼ぶとさらに古いもの。</summary>
    public EditorLocation? Previous(EditorLocation? current, Func<EditorLocation, bool>? isAvailable = null)
    {
        var start = _cursor < 0 ? _entries.Count - 1 : _cursor - 1;
        for (var i = start; i >= 0; i--)
        {
            var entry = _entries[i];
            if (_cursor < 0 && current is { } now && entry.IsNear(now, EditorLocationHistory.JumpThresholdLines))
                continue;
            if (isAvailable is not null && !isAvailable(entry))
                continue;
            _cursor = i;
            return entry;
        }
        return null;
    }

    public void Clear()
    {
        _entries.Clear();
        _cursor = -1;
    }
}
