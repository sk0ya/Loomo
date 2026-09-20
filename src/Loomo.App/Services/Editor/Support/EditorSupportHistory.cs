using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// EditorSupport／エディタの「戻る・進む」ファイル履歴の純粋ロジック（WPF 非依存・テスト可能）。
/// エディタの現在ファイル（＝EditorSupport の追従ソース）が切り替わるたびに <see cref="Navigate"/> で記録し、
/// <see cref="GoBack"/>／<see cref="GoForward"/> でファイル間を行き来する。ブラウザの履歴と同じく
/// <c>current</c> ポインタと back/forward の 2 スタックで表現し、通常の遷移（<see cref="Navigate"/>）は
/// forward を捨てる。パスは大小無視で比較する（Windows のファイルシステム前提）。
/// </summary>
public sealed class EditorSupportHistory
{
    private readonly List<string> _back = new();
    private readonly List<string> _forward = new();
    private string? _current;
    private readonly int _capacity;

    public EditorSupportHistory(int capacity = 100)
    {
        _capacity = capacity < 1 ? 1 : capacity;
    }

    /// <summary>戻れる履歴があるか。</summary>
    public bool CanGoBack => _back.Count > 0;

    /// <summary>進める履歴があるか。</summary>
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>
    /// 通常のファイル切替（戻る/進む以外）を記録する。現在位置を back へ積み、forward を捨てる。
    /// 空/null や現在位置と同一（連続重複）は無視する。back が上限を超えたら最古を落とす。
    /// </summary>
    public void Navigate(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        if (string.Equals(path, _current, StringComparison.OrdinalIgnoreCase))
            return;

        if (_current is not null)
        {
            _back.Add(_current);
            if (_back.Count > _capacity)
                _back.RemoveAt(0);
        }

        _forward.Clear();
        _current = path;
    }

    /// <summary>現在位置を forward へ退避し、back を 1 つ pop して現在位置にする。空なら null。</summary>
    public string? GoBack()
    {
        if (_back.Count == 0)
            return null;

        var prev = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        if (_current is not null)
            _forward.Add(_current);
        _current = prev;
        return prev;
    }

    /// <summary>現在位置を back へ積み、forward を 1 つ pop して現在位置にする。空なら null。</summary>
    public string? GoForward()
    {
        if (_forward.Count == 0)
            return null;

        var next = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        if (_current is not null)
            _back.Add(_current);
        _current = next;
        return next;
    }

    /// <summary>
    /// 指定パスを back/forward の全出現位置から除去する（タブを閉じた等・大小無視）。
    /// 現在位置が同一なら現在位置を無しにする（以降の <see cref="Navigate"/> が back へ積まない）。
    /// </summary>
    public void Remove(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        _back.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _forward.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(_current, path, StringComparison.OrdinalIgnoreCase))
            _current = null;
    }

    /// <summary>履歴を全消去する（ワークスペース切替等）。</summary>
    public void Clear()
    {
        _back.Clear();
        _forward.Clear();
        _current = null;
    }
}
