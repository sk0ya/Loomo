using System.Collections.Generic;
using System.Windows.Input;

namespace sk0ya.Loomo.App.Input;

/// <summary>1 つのキー入力に対する解決結果。</summary>
/// <param name="Handled">そのキーを消費したか（WPF の <see cref="System.Windows.RoutedEventArgs.Handled"/> へ反映）。</param>
/// <param name="Execute">実行すべきコマンド Id（無ければ null）。</param>
public readonly record struct KeyResolution(bool Handled, string? Execute = null);

/// <summary>
/// キーバインドの状態機械（純ロジック・WPF イベント非依存）。実効バインド表と内部状態
/// （連鎖待ちの<see cref="Pending"/>プレフィックス・<see cref="Mode"/>＝リサイズ等のモーダル）から、
/// 1 打ごとに「消費するか／どのコマンドを実行するか」を決める。モード遷移は <see cref="Mode"/> の
/// 変化として呼び出し側が観測する（ヒント表示の開閉に使う）。
/// 既存 <c>OnPaneNavKey</c> の挙動（Ctrl+W プレフィックス → h/j/k/l・Shift+hjkl でリサイズモード・
/// z/x/v/s/q・モード中の bare 連打・Esc/Enter 退出）をデータ駆動で再現する。
/// </summary>
public sealed class KeyboardResolver
{
    private Dictionary<KeyChord, string> _singles = new();
    private Dictionary<KeyChord, Dictionary<KeyChord, string>> _prefixed = new();
    private HashSet<KeyChord> _prefixChords = new();
    // モード名 → (bare キー → コマンド Id)。リサイズモード中の修飾子なし連打を導出する。
    private Dictionary<string, Dictionary<Key, string>> _modeRepeat = new();

    /// <summary>連鎖の 1 打目（プレフィックス）を受けて 2 打目を待っている状態。null は通常。</summary>
    public KeyChord? Pending { get; private set; }

    /// <summary>現在のモーダル状態名（<see cref="CommandCatalog.ResizeMode"/> 等）。null は通常。</summary>
    public string? Mode { get; private set; }

    /// <summary>実効バインド表から照合用の索引を組み直す（バインド変更時に呼ぶ）。</summary>
    public void SetBindings(IReadOnlyDictionary<string, KeySequence> effective)
    {
        var singles = new Dictionary<KeyChord, string>();
        var prefixed = new Dictionary<KeyChord, Dictionary<KeyChord, string>>();
        var modeRepeat = new Dictionary<string, Dictionary<Key, string>>();

        foreach (var (id, seq) in effective)
        {
            if (seq.Count == 1)
            {
                singles[seq.First] = id;
            }
            else
            {
                if (!prefixed.TryGetValue(seq.First, out var second))
                    prefixed[seq.First] = second = new();
                second[seq.Chords[1]] = id;
            }

            if (CommandCatalog.Find(id)?.EntersMode is { } mode)
            {
                if (!modeRepeat.TryGetValue(mode, out var keys))
                    modeRepeat[mode] = keys = new();
                keys[seq.Last.Key] = id; // 修飾子は無視（モード中は bare キーで反復）
            }
        }

        _singles = singles;
        _prefixed = prefixed;
        _prefixChords = new HashSet<KeyChord>(prefixed.Keys);
        _modeRepeat = modeRepeat;

        // 待機中のプレフィックスがバインド変更で無効化されたら破棄する。
        if (Pending is { } p && !_prefixChords.Contains(p))
            Pending = null;
    }

    public void ClearPending() => Pending = null;

    /// <summary>モードを抜ける。抜けたとき true。</summary>
    public bool ExitMode()
    {
        if (Mode is null) return false;
        Mode = null;
        return true;
    }

    public void Reset()
    {
        Pending = null;
        Mode = null;
    }

    public KeyResolution Resolve(KeyChord chord)
    {
        // 1) モード中（リサイズ等）
        if (Mode is { } mode)
        {
            if (_prefixChords.Contains(chord))      // プレフィックスでモードを抜けて連鎖待ちへ
            {
                Mode = null;
                Pending = chord;
                return new KeyResolution(true);
            }
            if (_modeRepeat.TryGetValue(mode, out var keys) && keys.TryGetValue(chord.Key, out var repeatId))
                return new KeyResolution(true, repeatId);   // bare キーで反復（モード維持）

            Mode = null;                            // 対象外キー：モードを抜ける
            return new KeyResolution(chord.Key is Key.Escape or Key.Return); // Esc/Enter のみ消費
        }

        // 2) 連鎖の 2 打目待ち
        if (Pending is { } pending)
        {
            if (_prefixChords.Contains(chord))      // プレフィックス再入力 → 待ち継続
            {
                Pending = chord;
                return new KeyResolution(true);
            }

            Pending = null;
            // プレフィックス自身の修飾子は剥がして照合（Ctrl を押しっぱなしでも 2 打目を拾う）。
            var lookup = new KeyChord(chord.Modifiers & ~pending.Modifiers, chord.Key);
            if (_prefixed.TryGetValue(pending, out var second)
                && (second.TryGetValue(lookup, out var id) || second.TryGetValue(chord, out id)))
                return new KeyResolution(true, EnterModeIfAny(id));

            return new KeyResolution(false);        // 対象外 → プレフィックス解除して素通し
        }

        // 3) 通常
        if (_prefixChords.Contains(chord))
        {
            Pending = chord;
            return new KeyResolution(true);
        }
        if (_singles.TryGetValue(chord, out var single))
            return new KeyResolution(true, EnterModeIfAny(single));

        return new KeyResolution(false);
    }

    private string EnterModeIfAny(string id)
    {
        if (CommandCatalog.Find(id)?.EntersMode is { } mode)
            Mode = mode;
        return id;
    }
}
