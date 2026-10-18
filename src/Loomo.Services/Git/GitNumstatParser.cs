using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.Services;

/// <summary>1ファイルの増減の行数。バイナリは行で数えられないので <see cref="IsBinary"/> だけ立つ。</summary>
public sealed record GitLineStat(int Added, int Deleted, bool IsBinary)
{
    /// <summary>一覧に添える文字（<c>+12 −3</c>）。バイナリは「bin」。</summary>
    public string DisplayText => IsBinary ? "bin" : $"+{Added} −{Deleted}";

    /// <summary>行に色分けして出す増えた側（<c>+12</c>）。バイナリは「bin」。</summary>
    public string AddedText => IsBinary ? "bin" : $"+{Added}";

    /// <summary>行に色分けして出す減った側（<c>−3</c>）。バイナリは空。</summary>
    public string DeletedText => IsBinary ? "" : $" −{Deleted}";
}

/// <summary>
/// <c>git diff --numstat -z</c> の出力を、パス（リポジトリのルートからの相対・<c>/</c> 区切り）→行数に読む（§24.32）。
/// <c>-z</c> で読むのは、パスに空白やタブや日本語があっても引用符で包まれずにそのまま来るから。
/// 名前の変更・複製は「行数\t行数\t\0元のパス\0新しいパス\0」の 3 つ組で来るので、<b>新しいパス</b>に付ける
/// （変更一覧の行は新しいパスで並ぶ）。
/// </summary>
public static class GitNumstatParser
{
    public static IReadOnlyDictionary<string, GitLineStat> Parse(string output)
    {
        var stats = new Dictionary<string, GitLineStat>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(output)) return stats;

        var tokens = output.Split('\0');
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i].TrimStart('\n', '\r');
            if (token.Length == 0) continue;
            var parts = token.Split('\t', 3);
            if (parts.Length < 3) continue;

            var binary = parts[0] == "-" && parts[1] == "-";
            var added = binary ? 0 : ParseCount(parts[0]);
            var deleted = binary ? 0 : ParseCount(parts[1]);
            if (added is null || deleted is null) continue;

            string path;
            if (parts[2].Length == 0)
            {
                // 名前の変更：次の 2 つが元と新しいパス。
                if (i + 2 >= tokens.Length) break;
                path = tokens[i + 2];
                i += 2;
            }
            else
            {
                path = parts[2];
            }
            stats[path] = new GitLineStat(added.Value, deleted.Value, binary);
        }
        return stats;
    }

    private static int? ParseCount(string text) => int.TryParse(text, out var n) && n >= 0 ? n : null;
}
