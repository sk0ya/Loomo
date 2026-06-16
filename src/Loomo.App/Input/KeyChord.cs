using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace sk0ya.Loomo.App.Input;

/// <summary>
/// 1 つのキー押下（修飾子＋キー）を表す不変値。例: <c>Ctrl+Shift+P</c>、<c>Ctrl+W</c>。
/// 文字列との相互変換はカルチャ非依存で、設定ファイルへの保存・キャプチャ表示・既定値定義に使う。
/// 修飾子のみ（Ctrl 等の単独押下）は妥当なジェスチャではないので生成・パースともに弾く。
/// </summary>
public readonly record struct KeyChord(ModifierKeys Modifiers, Key Key)
{
    /// <summary>そのキーが修飾子そのもの（Ctrl/Shift/Alt/Win）か。単独ではジェスチャにならない。</summary>
    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or Key.System or Key.LWin or Key.RWin;

    /// <summary>"Ctrl+Shift+P" のような表記へ整形する（修飾子は Ctrl→Alt→Shift→Win の固定順）。</summary>
    public string Format()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(FormatKey(Key));
        return string.Join("+", parts);
    }

    public override string ToString() => Format();

    /// <summary>WPF のキーイベントから chord を作る。修飾子のみ・None・IME 合成中は null（ジェスチャ未確定）。
    /// Alt 併用時の <see cref="Key.System"/> は実キー（<see cref="KeyEventArgs.SystemKey"/>）へ読み替える。
    /// IME 合成中のキーは <see cref="Key.ImeProcessed"/> で届くので、ジェスチャ・バインドキャプチャ
    /// いずれの対象にもしない（合成を妨げない）。</summary>
    public static KeyChord? FromEvent(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.None || key == Key.ImeProcessed || IsModifierKey(key)) return null;
        return new KeyChord(Keyboard.Modifiers, key);
    }

    /// <summary>"Ctrl+Shift+P" 等をパースする。空・不正・修飾子のみは null。</summary>
    public static KeyChord? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var tokens = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return null;

        var mods = ModifierKeys.None;
        Key? key = null;
        foreach (var token in tokens)
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win" or "windows" or "meta": mods |= ModifierKeys.Windows; break;
                default:
                    if (key is not null) return null;          // 非修飾キーが 2 つ以上は不正
                    if (ParseKey(token) is not { } parsed) return null;
                    key = parsed;
                    break;
            }
        }

        if (key is null || IsModifierKey(key.Value)) return null;
        return new KeyChord(mods, key.Value);
    }

    // ===== キー名 ⇄ 表記。よく使うキーは短く読みやすい名前に寄せ、残りは enum 名にフォールバック。 =====

    private static string FormatKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (key - Key.NumPad0),
        >= Key.F1 and <= Key.F24 => key.ToString(),
        Key.Return => "Enter",
        Key.Escape => "Esc",
        Key.Space => "Space",
        Key.Tab => "Tab",
        Key.Back => "Backspace",
        Key.Delete => "Delete",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        _ => key.ToString()
    };

    private static Key? ParseKey(string token)
    {
        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z') return Key.A + (c - 'A');
            if (c is >= '0' and <= '9') return Key.D0 + (c - '0');
            return c switch
            {
                ',' => Key.OemComma,
                '.' => Key.OemPeriod,
                '/' => Key.OemQuestion,
                '-' => Key.OemMinus,
                '=' => Key.OemPlus,
                _ => null
            };
        }

        return token.ToLowerInvariant() switch
        {
            "enter" or "return" => Key.Return,
            "esc" or "escape" => Key.Escape,
            "space" => Key.Space,
            "tab" => Key.Tab,
            "backspace" or "back" => Key.Back,
            "delete" or "del" => Key.Delete,
            "left" => Key.Left,
            "right" => Key.Right,
            "up" => Key.Up,
            "down" => Key.Down,
            _ when token.StartsWith("num", StringComparison.OrdinalIgnoreCase)
                   && int.TryParse(token.AsSpan(3), out var n) && n is >= 0 and <= 9
                => Key.NumPad0 + n,
            _ => Enum.TryParse<Key>(token, ignoreCase: true, out var parsed) ? parsed : null
        };
    }
}
