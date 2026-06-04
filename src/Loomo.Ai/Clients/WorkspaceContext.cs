using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// システムプロンプトへ動的に添える「現在のフォルダ」情報を組み立てる。
/// ルートパス・選択中パスに加え、ルート直下の一覧（生成物は除外・上限あり）を含めて
/// モデルが毎ターン現在地を把握できるようにする。設定値そのものは変更しない。
/// </summary>
internal static class WorkspaceContext
{
    private const int MaxTopLevelEntries = 60;

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages"
    };

    /// <summary>システムプロンプト末尾へ連結する文字列を返す。フォルダ未オープン時は空。</summary>
    public static string Describe(IWorkspaceService workspace)
    {
        var root = workspace.RootPath;
        if (string.IsNullOrWhiteSpace(root)) return string.Empty;

        var sb = new StringBuilder();
        sb.Append("\n\n# 現在のフォルダ\n");
        sb.Append("ワークスペースルート: ").Append(root).Append('\n');

        var selected = workspace.SelectedPath;
        if (!string.IsNullOrWhiteSpace(selected))
            sb.Append("FolderTree で選択中: ").Append(selected).Append('\n');

        var entries = TopLevelEntries(root);
        if (entries.Count > 0)
        {
            sb.Append("ルート直下（bin/obj/.git 等の生成物は除外）:\n");
            foreach (var entry in entries)
                sb.Append("  ").Append(entry).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>ルート直下のフォルダ→ファイルを名前昇順で。生成物フォルダは除外し件数を制限する。</summary>
    private static List<string> TopLevelEntries(string root)
    {
        var result = new List<string>();
        try
        {
            foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                if (SkipDirectories.Contains(name)) continue;
                result.Add(name + "/");
                if (result.Count >= MaxTopLevelEntries) return result;
            }

            foreach (var file in Directory.GetFiles(root).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(Path.GetFileName(file));
                if (result.Count >= MaxTopLevelEntries) return result;
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }

        return result;
    }
}
