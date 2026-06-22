using System;
using System.Collections.Generic;
using System.Text;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// デコードループの repetition collapse（繰り返し暴走）を決定論的に止めるガード。ONNX / llama.cpp の
/// どちらのエンジンでも同じ基準で停止させるため共有する（ORT の no_repeat_ngram_size は CPU で無視され、
/// llama.cpp も repetition_penalty だけでは長周期反復を確実には止められないため、この保険を常に効かせる）。
/// </summary>
public static class DecodeLoopGuards
{
    /// <summary>
    /// 末尾が短周期の繰り返しループに陥っているか（repetition collapse の検知）。長さ <paramref name="maxUnit"/>
    /// 以下の繰り返し単位が末尾で <paramref name="minRepeats"/> 回以上連続していれば true。" . " のような
    /// 1〜数トークンの暴走を捕まえる。エージェント／ツール用途では短周期の多数回反復はまず崩壊なので、
    /// 正常な短い反復を巻き込まないよう繰り返し回数のしきい値は高めに取る。
    /// </summary>
    public static bool IsLoopingTail(IReadOnlyList<int> g, int maxUnit = 8, int minRepeats = 10)
    {
        for (var unit = 1; unit <= maxUnit; unit++)
        {
            var need = unit * minRepeats;
            if (g.Count < need) continue;

            var looping = true;
            for (var k = 1; k < minRepeats && looping; k++)
                for (var j = 0; j < unit; j++)
                    if (g[g.Count - 1 - j] != g[g.Count - 1 - j - k * unit]) { looping = false; break; }

            if (looping) return true;
        }
        return false;
    }

    /// <summary>
    /// デコード済みテキスト末尾で、同じ文章ブロックが連続しているかを検出する。
    /// トークン単位では捕まえにくい、数十〜数百文字の回答ブロック反復を止めるための保険。
    /// </summary>
    public static bool IsRepeatingTextTail(StringBuilder text, int minUnitChars = 24, int maxUnitChars = 600, int minRepeats = 3)
    {
        var len = text.Length;
        if (len < minUnitChars * minRepeats) return false;

        var maxUnit = Math.Min(maxUnitChars, len / minRepeats);
        for (var unit = minUnitChars; unit <= maxUnit; unit++)
        {
            var repeated = true;
            for (var r = 1; r < minRepeats && repeated; r++)
            {
                var a = len - unit;
                var b = len - unit * (r + 1);
                for (var i = 0; i < unit; i++)
                {
                    if (text[a + i] == text[b + i]) continue;
                    repeated = false;
                    break;
                }
            }

            if (repeated) return true;
        }

        return false;
    }
}
