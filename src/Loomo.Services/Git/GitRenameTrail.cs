using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.Services;

/// <summary>ファイル履歴を <c>--follow</c> で辿ったときの「そのコミット時点でのパス」表。
///
/// <para>リネームを追う履歴には<b>いまの名前で存在しなかったコミット</b>が並ぶ。そこへ
/// <c>git show &lt;hash&gt;:&lt;いまのパス&gt;</c> を投げると必ず「このファイルはありません」になるので、
/// 版を開く・比べる・戻すといった操作はリネーム前の行で軒並み失敗する。追跡で得た旧名を
/// コミットごとに覚えておき、その版のパスで引けるようにするための表。</para></summary>
public static class GitRenameTrail
{
    /// <summary><c>git log --follow --format=%H --name-status -- &lt;path&gt;</c> の出力を、
    /// コミットハッシュ→そのコミット時点のパス、に読み替える。
    ///
    /// <para>出力は新しい順。ハッシュ行を見たらその時点のパスを記録し、リネーム
    /// （<c>R100\t旧\t新</c>）を見たら<b>それより古い側</b>のパスを旧名へ切り替える——
    /// リネームしたコミット自身は「新しい名前になった版」なので、切り替えは記録の後で行う。</para></summary>
    public static IReadOnlyDictionary<string, string> Parse(string output, string currentPath)
    {
        var trail = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(currentPath))
            return trail;

        var path = currentPath;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;

            if (IsHash(line))
            {
                trail[line] = path;
                continue;
            }

            // name-status 行。3列目があるのがリネーム／コピーで、2列目が旧パス・3列目が新パス。
            var parts = line.Split('\t');
            if (parts.Length < 3 || parts[0].Length == 0 || parts[0][0] is not ('R' or 'C')) continue;
            if (parts[1].Length == 0 || !string.Equals(parts[2], path, StringComparison.OrdinalIgnoreCase))
                continue;
            path = parts[1];
        }
        return trail;
    }

    /// <summary>ハッシュ行か（name-status 行は必ずタブを含むので、それだけで見分けがつく）。</summary>
    private static bool IsHash(string line)
    {
        if (line.Length is < 7 or > 64 || line.IndexOf('\t') >= 0) return false;
        foreach (var c in line)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }
}
