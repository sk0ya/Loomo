using System.Collections.Generic;
using System.Text;

namespace sk0ya.Loomo.Core.Debug;

/// <summary>デバッグ起動の入力欄（引数・環境変数）を <see cref="DebugLaunchConfig"/> 用に解釈する純粋ヘルパ。
/// UI 非依存・副作用なしで、<c>DebugViewModel</c> から使い、単体テストで挙動を固定する。</summary>
public static class DebugLaunchArgs
{
    /// <summary>コマンドライン風の 1 行を引数トークンへ分割する。空白区切りで、二重引用符 <c>"…"</c> で
    /// 囲んだ範囲は 1 トークンとして空白を含められる（引用符自体はトークンに含めない）。空・空白のみなら空配列。</summary>
    public static IReadOnlyList<string> ParseArgs(string? line)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(line)) return result;

        var sb = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;  // 空文字 "" も 1 トークンとして拾うためのフラグ

        foreach (var ch in line)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;  // 引用符が出たら（中身が空でも）トークン確定
            }
            else if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (hasToken) { result.Add(sb.ToString()); sb.Clear(); hasToken = false; }
            }
            else
            {
                sb.Append(ch);
                hasToken = true;
            }
        }
        if (hasToken) result.Add(sb.ToString());
        return result;
    }

    /// <summary>1 行 1 件の <c>KEY=VALUE</c> を環境変数辞書へ解釈する。<c>=</c> を含まない行・空行は無視し、
    /// キーは前後空白を除く（値は最初の <c>=</c> 以降をそのまま）。同名キーは後勝ち。空なら null（環境を変えない）。</summary>
    public static IReadOnlyDictionary<string, string>? ParseEnv(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var dict = new Dictionary<string, string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t');
            if (line.Length == 0 || line.StartsWith('#')) continue;  // 空行・コメント行は無視
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;  // '=' 無し／先頭が '=' は無視
            var key = line[..eq].Trim();
            if (key.Length == 0) continue;
            dict[key] = line[(eq + 1)..];
        }
        return dict.Count > 0 ? dict : null;
    }
}
