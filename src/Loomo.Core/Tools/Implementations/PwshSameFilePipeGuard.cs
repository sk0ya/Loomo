using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Tools.Implementations;

/// <summary>
/// 「同一ファイルを Get-Content で読みながら、同じパイプラインで書き戻す」PowerShell の footgun を
/// 実行前に検出する。<c>Get-Content x | … | Set-Content x</c> はストリーミング読みの最中に書き込みが
/// 始まるため「別のプロセスで使用中」エラーや内容消失を起こす（小モデルが multi-step / delete-line 系で
/// 頻発させる主要故障。エラー後も同型を再生産して反復を浪費する）。
/// 括弧で全読みしてから流す <c>(Get-Content x) | Set-Content x</c> と、変数へ読んでから書く
/// <c>$c = Get-Content x; Set-Content x $c</c> は安全なので対象外。
/// 検出はヒューリスティック（クオート内の ; | は考慮しない）だが、ブロックは回復可能なツールエラー
/// （安全な書き方と edit_file を案内）なので、まれな誤検出は再試行1回で済む。
/// </summary>
internal static class PwshSameFilePipeGuard
{
    private static readonly Regex ReadCmdlet = new(@"\bGet-Content\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WriteCmdlet = new(@"\b(?:Set-Content|Add-Content|Out-File)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TokenPattern = new(@"""([^""]*)""|'([^']*)'|([^\s""'|;()]+)",
        RegexOptions.Compiled);

    /// <summary>同一ファイルへの read|write パイプを検出したら、その（正規化済み）パスを返す。無ければ null。</summary>
    public static string? DetectSameFileReadWrite(string command)
    {
        foreach (var statement in command.Split(';'))
        {
            var segments = statement.Split('|');
            if (segments.Length < 2) continue;

            // パイプへ流している（＝読み終わる前に後段が走り出す）Get-Content の対象パス。
            var readPaths = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];

                // 後段の書き込み cmdlet：前段までにストリーミング読みしたファイルと同じなら footgun。
                if (readPaths.Count > 0)
                {
                    var write = WriteCmdlet.Match(segment);
                    if (write.Success)
                        foreach (var path in PathTokens(segment, write.Index + write.Length))
                            if (readPaths.Contains(path))
                                return path;
                }

                // 最終段の Get-Content はパイプへ流さないので対象外。
                // 手前に "(" があれば括弧が全読みを完了させてから流れるので安全。
                if (i == segments.Length - 1) continue;
                var read = ReadCmdlet.Match(segment);
                if (!read.Success || segment.AsSpan(0, read.Index).Contains('(')) continue;
                foreach (var path in PathTokens(segment, read.Index + read.Length))
                    readPaths.Add(path);
            }
        }
        return null;
    }

    /// <summary>cmdlet 名の直後からパスらしきトークンを列挙する。-で始まるパラメータ名は除外し、
    /// クオートを剥がして大文字小文字と / \ を正規化（同一ファイル比較のキーに揃える）。</summary>
    private static IEnumerable<string> PathTokens(string segment, int fromIndex)
    {
        foreach (Match m in TokenPattern.Matches(segment[fromIndex..]))
        {
            var raw = m.Groups[1].Success ? m.Groups[1].Value
                : m.Groups[2].Success ? m.Groups[2].Value
                : m.Groups[3].Value;
            if (raw.Length == 0 || raw[0] == '-') continue;
            yield return raw.Replace('/', '\\').ToLowerInvariant();
        }
    }
}
