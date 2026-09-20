using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.Services;

/// <summary>
/// <c>git log --graph --pretty=format:%x1f…</c> の出力をパースする。
/// グラフ部分（"* | \\" 等）と US(0x1F) 区切りのメタ情報を行ごとに分離し、
/// メタ情報を持たない行は枝の継続行（<see cref="GitLogRow.IsCommit"/> = false）として保持する。
/// </summary>
public static class GitLogParser
{
    /// <summary>GitService.GetLogAsync が使う --pretty 書式（グラフ列の直後に US 区切りで並ぶ）。
    /// 末尾の <c>%P</c>（親ハッシュ・空白区切り）はグラフのレーンを自分で組むために要る
    /// （<see cref="GitCommitGraph"/>）。<b>末尾に足す</b>ので、古い並びを読む箇所は壊れない。</summary>
    public const string PrettyFormat = "%x1f%H%x1f%h%x1f%an%x1f%ad%x1f%D%x1f%s%x1f%P";

    public static IReadOnlyList<GitLogRow> Parse(string output)
    {
        var rows = new List<GitLogRow>();
        foreach (var line in output.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.Length == 0)
                continue;

            var sep = l.IndexOf('\x1f');
            if (sep < 0)
            {
                rows.Add(new GitLogRow(l, null, null, null, null, null, null));
                continue;
            }

            var graph = l[..sep].TrimEnd();
            var fields = l[(sep + 1)..].Split('\x1f');
            if (fields.Length < 6)
            {
                rows.Add(new GitLogRow(graph, null, null, null, null, null, null));
                continue;
            }

            rows.Add(new GitLogRow(
                graph,
                Hash: fields[0],
                ShortHash: fields[1],
                Author: fields[2],
                Date: fields[3],
                Refs: fields[4].Length > 0 ? fields[4] : null,
                Subject: fields[5])
            {
                // 親が無い（最初のコミット）と、書式が古い出力の両方で空になる。
                ParentsText = fields.Length > 6 ? fields[6].Trim() : "",
            });
        }
        return rows;
    }
}
