using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace sk0ya.Loomo.App.Input;

/// <summary>
/// WPF のキーイベントを <see cref="KeyboardResolver"/> へ橋渡しする。<see cref="KeyEventArgs"/> を
/// <see cref="KeyChord"/> へ変換し、解決結果に従ってコマンド（Id→アクション）を実行・
/// <see cref="KeyEventArgs.Handled"/> を設定し、モード遷移をコールバックで知らせる。実効バインドが
/// 変わったら（<see cref="KeybindingService.Changed"/>）索引を組み直す。
/// </summary>
public sealed class KeyboardDispatcher
{
    private readonly KeyboardResolver _resolver = new();
    private readonly KeybindingService _bindings;
    private readonly IReadOnlyDictionary<string, Action> _actions;
    private readonly Action<string>? _onEnterMode;
    private readonly Action<string>? _onExitMode;

    /// <param name="actions">コマンド Id → 実体アクション（ShellWindow が結線）。</param>
    /// <param name="onEnterMode">モーダル状態に入ったとき（例: リサイズモードのヒント表示）。</param>
    /// <param name="onExitMode">モーダル状態を抜けたとき。</param>
    public KeyboardDispatcher(
        KeybindingService bindings,
        IReadOnlyDictionary<string, Action> actions,
        Action<string>? onEnterMode = null,
        Action<string>? onExitMode = null)
    {
        _bindings = bindings;
        _actions = actions;
        _onEnterMode = onEnterMode;
        _onExitMode = onExitMode;
        _bindings.Changed += Rebuild;
        Rebuild();
    }

    private void Rebuild() => _resolver.SetBindings(_bindings.Effective);

    public void HandlePreviewKeyDown(KeyEventArgs e)
    {
        if (KeyChord.FromEvent(e) is not { } chord) return;   // 修飾子のみ：状態を保ったまま素通し

        var prevMode = _resolver.Mode;
        var res = _resolver.Resolve(chord);
        ApplyModeTransition(prevMode);

        if (res.Execute is { } id)
        {
            // アクション未登録のコマンド（別スコープ＝コンポーザ等）は消費せず素通しし、
            // より内側のハンドラに委ねる。登録済みのときだけ実行＆消費する。
            if (_actions.TryGetValue(id, out var action))
            {
                action();
                if (res.Handled) e.Handled = true;
            }
        }
        else if (res.Handled)
        {
            e.Handled = true;
        }
    }

    /// <summary>外部要因（クリック等）でフォーカスが移ったときに呼ぶ。連鎖待ちは常に解除し、
    /// モードは抑止中でなければ抜ける（リサイズ自身が起こすフォーカス移動で抜けないようにするため）。</summary>
    public void OnExternalFocusChange(bool suppressModeExit)
    {
        _resolver.ClearPending();
        if (suppressModeExit) return;
        var prev = _resolver.Mode;
        if (_resolver.ExitMode() && prev is { } m)
            _onExitMode?.Invoke(m);
    }

    /// <summary>待ち状態・モードをすべて解除する（ウィンドウ非アクティブ化など）。</summary>
    public void Reset()
    {
        var prev = _resolver.Mode;
        _resolver.Reset();
        if (prev is { } m)
            _onExitMode?.Invoke(m);
    }

    private void ApplyModeTransition(string? prevMode)
    {
        var now = _resolver.Mode;
        if (now == prevMode) return;
        if (now is { } entered)
            _onEnterMode?.Invoke(entered);
        else if (prevMode is { } exited)
            _onExitMode?.Invoke(exited);
    }
}
