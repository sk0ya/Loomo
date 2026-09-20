using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.Services;

/// <summary>
/// <c>git status --porcelain=v2 --branch</c> の出力をパースする。
/// v2 形式（行頭 1/2/u/?/!/#）はロケール・設定に依存せず安定している。
/// </summary>
public static class GitStatusParser
{
    public static GitStatusSnapshot Parse(string output)
    {
        var branch = "";
        string? upstream = null;
        var ahead = 0;
        var behind = 0;
        var staged = new List<GitChangeEntry>();
        var unstaged = new List<GitChangeEntry>();

        foreach (var line in output.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.Length == 0)
                continue;

            switch (l[0])
            {
                case '#':
                    ParseHeader(l, ref branch, ref upstream, ref ahead, ref behind);
                    break;
                case '1':
                {
                    // 1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>
                    var parts = l.Split(' ', 9);
                    if (parts.Length < 9) break;
                    AddEntry(staged, unstaged, new GitChangeEntry(
                        parts[8], null, parts[1][0], parts[1][1], false, false));
                    break;
                }
                case '2':
                {
                    // 2 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <X><score> <path>\t<origPath>
                    var parts = l.Split(' ', 10);
                    if (parts.Length < 10) break;
                    var tab = parts[9].IndexOf('\t');
                    var path = tab >= 0 ? parts[9][..tab] : parts[9];
                    var orig = tab >= 0 ? parts[9][(tab + 1)..] : null;
                    AddEntry(staged, unstaged, new GitChangeEntry(
                        path, orig, parts[1][0], parts[1][1], false, false));
                    break;
                }
                case 'u':
                {
                    // u <XY> <sub> <m1> <m2> <m3> <mW> <h1> <h2> <h3> <path>
                    var parts = l.Split(' ', 11);
                    if (parts.Length < 11) break;
                    unstaged.Add(new GitChangeEntry(
                        parts[10], null, parts[1][0], parts[1][1], false, true));
                    break;
                }
                case '?':
                    if (l.Length > 2)
                        unstaged.Add(new GitChangeEntry(l[2..], null, '.', '?', true, false));
                    break;
            }
        }

        return new GitStatusSnapshot
        {
            IsRepository = true,
            Branch = branch,
            Upstream = upstream,
            Ahead = ahead,
            Behind = behind,
            Staged = staged,
            Unstaged = unstaged,
        };
    }

    private static void ParseHeader(
        string line, ref string branch, ref string? upstream, ref int ahead, ref int behind)
    {
        if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
        {
            branch = line["# branch.head ".Length..];
        }
        else if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal))
        {
            upstream = line["# branch.upstream ".Length..];
        }
        else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
        {
            // "# branch.ab +1 -2"
            foreach (var token in line["# branch.ab ".Length..].Split(' '))
            {
                if (token.Length < 2) continue;
                if (token[0] == '+' && int.TryParse(token[1..], out var a)) ahead = a;
                else if (token[0] == '-' && int.TryParse(token[1..], out var b)) behind = b;
            }
        }
    }

    /// <summary>X（インデックス側）に変更があればステージ済みへ、Y（作業ツリー側）にあれば未ステージへ。</summary>
    private static void AddEntry(
        List<GitChangeEntry> staged, List<GitChangeEntry> unstaged, GitChangeEntry entry)
    {
        if (entry.IndexStatus != '.')
            staged.Add(entry);
        if (entry.WorkStatus != '.')
            unstaged.Add(entry);
    }
}
