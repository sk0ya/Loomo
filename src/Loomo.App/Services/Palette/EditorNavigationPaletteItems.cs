using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// エディタのナビゲーション一覧（最近のファイル／最近の場所／ファイル構造）をパレットの行へ写す
/// 純ロジック（テスト対象）。どの行も <see cref="PaletteCommand.Target"/> を持つので、選んでいる間は
/// 右の欄に中身が出て、Enter で同じ場所へ飛ぶ。
/// </summary>
public static class EditorNavigationPaletteItems
{
    public const string RecentFilesCategory = "最近のファイル";
    public const string RecentLocationsCategory = "最近の場所";

    /// <summary>最近のファイル（新しい順）。いま開いているファイルは除く——1行目が「直前のファイル」になり、
    /// 開いて Enter だけで行き来できる（Rider の Recent Files と同じ）。</summary>
    public static IReadOnlyList<PaletteCommand> ForRecentFiles(
        IEnumerable<string> recentPaths, string? activePath,
        Func<string, string> toDisplayPath, Func<PaletteTarget, Action> jump)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(activePath))
            seen.Add(activePath);
        var result = new List<PaletteCommand>();
        foreach (var path in recentPaths)
        {
            if (string.IsNullOrEmpty(path) || !seen.Add(path))
                continue;
            var target = new PaletteTarget(path);
            result.Add(new PaletteCommand(FolderOf(toDisplayPath(path)), Path.GetFileName(path), jump(target))
            {
                Target = target,
            });
        }
        return result;
    }

    /// <summary>最近の場所。行の文面（<paramref name="lineText"/>）が取れればそれを名前にし、
    /// 取れなければ「ファイル名:行」にする。</summary>
    public static IReadOnlyList<PaletteCommand> ForLocations(
        IReadOnlyList<EditorLocation> locations, Func<EditorLocation, string?> lineText,
        Func<string, string> toDisplayPath, Func<PaletteTarget, Action> jump)
    {
        var result = new List<PaletteCommand>(locations.Count);
        foreach (var location in locations)
        {
            var target = new PaletteTarget(location.Path, location.Line + 1, location.Column + 1);
            var text = lineText(location)?.Trim();
            var title = string.IsNullOrEmpty(text) ? $"{Path.GetFileName(location.Path)}:{location.Line + 1}" : text;
            result.Add(new PaletteCommand($"{toDisplayPath(location.Path)}:{location.Line + 1}", title, jump(target))
            {
                Target = target,
            });
        }
        return result;
    }

    /// <summary>ファイル構造（Rider の File Structure）。宣言順のまま入れ子を字下げで示し、
    /// 種類（method／property…）と、取れていればシグネチャを添える。</summary>
    public static IReadOnlyList<PaletteCommand> ForStructure(
        IReadOnlyList<OutlineNode> roots, string filePath, Func<PaletteTarget, Action> jump)
    {
        var result = new List<PaletteCommand>();
        void Walk(IReadOnlyList<OutlineNode> nodes, int depth)
        {
            foreach (var node in nodes)
            {
                var target = new PaletteTarget(filePath, node.NameLine0 + 1, node.NameCol0 + 1);
                var indent = new string(' ', depth * 2);
                var detail = string.IsNullOrWhiteSpace(node.Detail) ? "" : " " + node.Detail.Trim();
                result.Add(new PaletteCommand(CodeOutline.KindBadge(node.Kind).Title, indent + node.Name + detail,
                    jump(target))
                {
                    Target = target,
                    MatchTitleOnly = true,
                });
                Walk(node.Children, depth + 1);
            }
        }
        Walk(roots, 0);
        return result;
    }

    /// <summary>キャレットを含むいちばん内側の宣言の位置（一覧の初期選択に使う）。無ければ -1。</summary>
    public static int IndexOfEnclosing(IReadOnlyList<PaletteCommand> structure, IReadOnlyList<OutlineNode> roots,
        int caretLine0, int caretColumn0)
    {
        if (CodeOutline.FindEnclosing(roots, caretLine0, caretColumn0) is not { } member)
            return -1;
        for (var i = 0; i < structure.Count; i++)
        {
            if (structure[i].Target is { } t && t.Line == member.NameLine0 + 1 && t.Column == member.NameCol0 + 1)
                return i;
        }
        return -1;
    }

    private static string FolderOf(string displayPath)
    {
        var normalized = displayPath.Replace('\\', '/');
        var index = normalized.LastIndexOf('/');
        return index < 0 ? "" : normalized[..index];
    }
}
