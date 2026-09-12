using System;
using System.Linq;

namespace sk0ya.Loomo.Ai.Completion;

/// <summary>
/// FIM モデルの生の出力を、キャレットの先に薄く出してよい 1 行に直す。出せないと判断したら null。
///
/// <para>ここが効き目を決める。実測した 0.5B は「それらしいが違う」を平気で出す——
/// <c>ClearDiagnostics(e);</c> の代わりに <c>ClearDiagnostics(e.Uri, e.Diagnostics);</c> のように、
/// 形は正しく引数だけが違う。モデルを大きくするより落とす側を厚くする方が効く、というのは
/// JetBrains の Full Line Code Completion も同じ結論に達している（「正しくない候補を捨てて
/// 良い候補を 1% 失う代わりに、構文的な正しさを保証する」）。</para>
///
/// <para>ここで見るのは<b>文字列として明らかに壊れているもの</b>だけ。意味の検査
/// （未解決の参照、引数の数）は言語サーバーの仕事で、別の層で行う。</para>
/// </summary>
public static class FimCandidateFilter
{
    /// <summary>これ未満の長さで、かつ語が 2 つ未満なら出さない（記号だけの断片は読む価値がない）。</summary>
    private const int ShortCandidateLength = 8;

    /// <summary>同じ文字がこれ以上続いたら生成が崩れていると見なす。</summary>
    private const int MaxRepeatedChar = 8;

    /// <summary>同じ語がこれ以上続いたら生成が崩れていると見なす。</summary>
    private const int MaxRepeatedWord = 3;

    /// <summary>
    /// 生の出力を提案へ直す。出せないときは null。
    /// </summary>
    /// <param name="raw">モデルの出力そのまま。</param>
    /// <param name="lineBeforeCaret">キャレット行のキャレットより前（インデントを含む）。</param>
    /// <param name="lineAfterCaret">キャレット行のキャレットより後ろ。</param>
    public static string? Clean(string? raw, string lineBeforeCaret, string lineAfterCaret)
    {
        if (string.IsNullOrEmpty(raw)) return null;

        var text = CutAtStopMarker(raw).TrimEnd();
        if (text.Length == 0) return null;
        if (text.Any(char.IsControl)) return null;

        // モデルが手掛かりを書き直しているだけのことがある（トークン境界の都合で起きる）。
        // そのまま入れると「ClearClearDiagnostics」になる。
        var clue = lineBeforeCaret.TrimStart();
        if (clue.Length > 0 && text.StartsWith(clue, StringComparison.Ordinal)) return null;

        // すでに行の後ろにあるものをもう一度書いている。
        var after = lineAfterCaret.Trim();
        if (after.Length > 0 && text.Trim() == after) return null;

        if (IsTooShort(text)) return null;
        if (HasRunawayRepetition(text)) return null;

        // 元から対応が取れていない行（複数行に渡る式の途中など）はそのまま通す。
        // 見たいのは「候補を入れたせいで壊れたか」だけ。
        if (BreaksPairs(lineBeforeCaret + text + lineAfterCaret)
            && !BreaksPairs(lineBeforeCaret + lineAfterCaret))
            return null;

        return text;
    }

    /// <summary>停止の印より後ろを落とす（改行以降は 1 行しか出さないので常に捨てる）。</summary>
    private static string CutAtStopMarker(string raw)
    {
        int cut = raw.Length;
        foreach (var marker in FimPrompt.StopMarkers)
        {
            int at = raw.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0 && at < cut) cut = at;
        }
        return raw[..cut].TrimEnd('\r');
    }

    private static bool IsTooShort(string text)
    {
        if (text.Trim().Length == 0) return true;
        if (text.Length >= ShortCandidateLength) return false;

        // 短くても語が 2 つあれば意味を持つ（"ics(e);" のような続きは読める）。
        int words = 0;
        bool inWord = false;
        foreach (var c in text)
        {
            bool isWord = char.IsLetterOrDigit(c) || c == '_';
            if (isWord && !inWord) words++;
            inWord = isWord;
        }
        return words < 2;
    }

    /// <summary>生成が同じところを回り始めた形。長い行ほど起きやすい。</summary>
    private static bool HasRunawayRepetition(string text)
    {
        int run = 1;
        for (int i = 1; i < text.Length; i++)
        {
            if (text[i] == text[i - 1] && !char.IsWhiteSpace(text[i]))
            {
                if (++run >= MaxRepeatedChar) return true;
            }
            else run = 1;
        }

        var words = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        int wordRun = 1;
        for (int i = 1; i < words.Length; i++)
        {
            if (string.Equals(words[i], words[i - 1], StringComparison.Ordinal))
            {
                if (++wordRun >= MaxRepeatedWord) return true;
            }
            else wordRun = 1;
        }
        return false;
    }

    /// <summary>
    /// 候補を入れた行で、括弧・引用符の対応が<b>元より悪化</b>していないか。
    ///
    /// <para>閉じ過剰（<c>)</c> が <c>(</c> より多い、引用符が奇数）だけを見る。開いたままは許す
    /// ——複数行に渡る式の途中では、開きっぱなしが正しい姿だから。</para>
    /// </summary>
    private static bool BreaksPairs(string line)
    {
        int round = 0, square = 0, curly = 0;
        bool inString = false, inChar = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '\\' && (inString || inChar)) { i++; continue; }   // エスケープ

            if (inString) { if (c == '"') inString = false; continue; }
            if (inChar) { if (c == '\'') inChar = false; continue; }

            // 行コメントより後ろは数えない。自然文のアポストロフィ（don't）や、
            // 閉じ括弧について書いた説明文で、正しい候補を弾いてしまう。
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;

            switch (c)
            {
                case '"': inString = true; break;
                case '\'': inChar = true; break;
                case '(': round++; break;
                case ')': if (--round < 0) return true; break;
                case '[': square++; break;
                case ']': if (--square < 0) return true; break;
                case '{': curly++; break;
                case '}': if (--curly < 0) return true; break;
            }
        }

        // 行内で開いた文字列が閉じないまま終わるのは壊れている（逐語文字列は別だが、
        // その場合そもそも候補が 1 行で完結しない）。
        return inString || inChar;
    }
}
