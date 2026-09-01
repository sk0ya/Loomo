using System;
using System.Collections.Generic;
using System.Linq;

namespace sk0ya.Loomo.CSharp.Testing;

/// <summary><c>dotnet test --list-tests</c> の公式テストアダプター出力を、Loomo共通の検出モデルへ変換する。
/// ソース走査とは別の明示的な再検出経路であり、実データを持つケース名はメソッド単位へまとめて
/// <see cref="DiscoveredTest.IsParameterized"/> へ反映する。</summary>
public static class TestAdapterOutputParser
{
    private const string EnglishMarker = "The following Tests are available:";
    private const string JapaneseMarker = "次のテストを使用できます:";
    private const string JapaneseMarkerAlt = "次のテストが利用可能です:";

    public static IReadOnlyList<DiscoveredTest> Parse(string output)
    {
        var result = new List<DiscoveredTest>();
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var collecting = false;
        foreach (var rawLine in (output ?? "").Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Equals(EnglishMarker, StringComparison.OrdinalIgnoreCase)
                || line.Equals(JapaneseMarker, StringComparison.Ordinal)
                || line.Equals(JapaneseMarkerAlt, StringComparison.Ordinal))
            {
                collecting = true;
                continue;
            }
            if (!collecting) continue;
            if (line.StartsWith("Total tests:", StringComparison.OrdinalIgnoreCase))
            {
                collecting = false;
                continue;
            }
            if (line.Length == 0 || IsOutputNoise(line)) continue;

            // xUnit/NUnitのケース名は Method(args) 形式で出る。既存のTRX集約と同じ
            // メソッド名へ戻すことで、公式検出とソース検出が二重行を作らない。
            var paren = line.IndexOf('(');
            var methodName = paren > 0 ? line[..paren] : line;
            if (!indexes.TryGetValue(methodName, out var index))
            {
                indexes[methodName] = result.Count;
                result.Add(new DiscoveredTest(methodName, paren > 0,
                    Cases: paren > 0 ? [line] : null));
                continue;
            }

            if (paren > 0)
            {
                var current = result[index];
                var cases = current.Cases?.ToList() ?? [];
                if (!cases.Contains(line, StringComparer.Ordinal)) cases.Add(line);
                result[index] = current with { IsParameterized = true, Cases = cases };
            }
        }
        return result;
    }

    private static bool IsOutputNoise(string line)
        => line.StartsWith("Test run for ", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("VSTest", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Warning", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Results File:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Attachments:", StringComparison.OrdinalIgnoreCase);
}
