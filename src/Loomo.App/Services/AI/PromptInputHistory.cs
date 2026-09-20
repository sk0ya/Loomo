using System.Collections.Generic;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// AI入力欄の送信履歴ナビゲーション（↑/↓）。履歴リスト・ナビ位置・退避した下書きを管理し、
/// 永続化は <see cref="PromptHistoryStore"/> に委ねる。UI に依存しないので単体テストできる。
/// 呼び出し側は戻り値の bool でキー消費を、out の文字列で入力欄の書き換えを決める。
/// </summary>
public sealed class PromptInputHistory
{
    private readonly PromptHistoryStore _store;
    private readonly List<string> _history = new();   // 送信済みプロンプト（古い→新しい）
    private int _cursor = -1;                          // ナビ位置（-1 = 未ナビ／編集中の下書き）
    private string _draft = "";                        // ナビ開始前に編集していた内容

    public PromptInputHistory(PromptHistoryStore store)
    {
        _store = store;
        _history.AddRange(store.Load());   // 前回までの送信履歴を引き継ぐ
    }

    /// <summary>送信したプロンプトを履歴に積む（直前と同一なら積まない）。ナビ状態はリセットする。</summary>
    public void Push(string text)
    {
        _cursor = -1;
        _draft = "";
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_history.Count > 0 && _history[^1] == text) return;
        _history.Add(text);
        // メモリ上の履歴もファイルと同じ上限で切り詰める（無制限な肥大を防ぐ）。
        if (_history.Count > _store.MaxEntries)
            _history.RemoveRange(0, _history.Count - _store.MaxEntries);
        try { _store.Save(_history); } catch { /* 保存失敗は会話を妨げない */ }
    }

    /// <summary>↑：ひとつ前の履歴へ。履歴があれば true（キーを消費）。
    /// <paramref name="recalled"/> が null のときは入力欄を変えない（既に最古）。</summary>
    public bool RecallPrevious(string currentInput, out string? recalled)
    {
        recalled = null;
        if (_history.Count == 0) return false;
        if (_cursor < 0)
        {
            _draft = currentInput;            // ナビ開始：いまの下書きを退避
            _cursor = _history.Count;
        }
        if (_cursor == 0) return true;        // 既に最古：消費はするが内容は変えない
        _cursor--;
        recalled = _history[_cursor];
        return true;
    }

    /// <summary>↓：ひとつ後の履歴（末尾を超えたら退避していた下書きへ戻す）。ナビ中なら true。</summary>
    public bool RecallNext(out string? recalled)
    {
        recalled = null;
        if (_cursor < 0) return false;        // ナビ中でなければ素通し
        if (_cursor >= _history.Count - 1)
        {
            _cursor = -1;
            recalled = _draft;                // 末尾を超えたら編集中の下書きへ
            return true;
        }
        _cursor++;
        recalled = _history[_cursor];
        return true;
    }

    /// <summary>手入力でナビゲーションを抜ける（次の ↑ は最新履歴から始まる）。</summary>
    public void ResetNavigation() => _cursor = -1;
}
