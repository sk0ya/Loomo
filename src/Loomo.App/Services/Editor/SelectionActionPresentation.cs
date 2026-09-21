using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Services;

internal enum SelectionCompareKind
{
    SelectionWithClipboard,
    EditorWithClipboard,
    SavedEditorWithBuffer,
}

internal sealed record SelectionComparePresentation(
    SelectionCompareKind Kind, string Label, string ToolTip, string LeftTitle);
internal sealed record SelectionBrowserSearchTarget(string Query, string Url, string Title);

/// <summary>選択アクションの表示文字列と入力整形。</summary>
internal static class SelectionActionPresentation
{
    private const int MaxSearchQueryLength = 300;

    public static string? DetachedWindowLinkHeader(LinkOpenTarget target) => target.Kind switch
    {
        LinkOpenTargetKind.Url => $"リンク先を別ウィンドウで開く（{UrlHost(target.Value)}）",
        LinkOpenTargetKind.File => $"リンク先を別ウィンドウで開く（{Path.GetFileName(target.Value)}）",
        _ => null,
    };

    public static (string Header, string ToolTip) EditorSendItem(SourceLocation location)
    {
        var name = Path.GetFileName(location.Path);
        var where = location.Line > 0 ? $"{name}:{location.Line}" : name;
        var target = location.Line > 0 ? $"{location.Path}:{location.Line}" : location.Path;
        return ($"エディタへ送る（{where}）", target);
    }

    public static string SelectionSourceLabel(string? path)
        => path is { Length: > 0 }
            ? $"選択範囲（{Path.GetFileName(path)}）"
            : "エディタの選択";

    /// <summary>選択内容と編集中ファイルから、Diffメニューに出す比較先を決める。</summary>
    public static IReadOnlyList<SelectionComparePresentation> CompareEntries(
        string sourceLabel, string selectedText, bool hasSelection,
        bool hasEditor, string? filePath, bool isModified)
    {
        var entries = new List<SelectionComparePresentation>();
        var path = filePath ?? "";
        if (hasSelection && !string.IsNullOrWhiteSpace(selectedText))
            entries.Add(new(
                SelectionCompareKind.SelectionWithClipboard,
                "選択範囲とクリップボードを比較",
                "選択テキストを左、クリップボードの内容を右に置いて Diff ペインで見比べる",
                sourceLabel));
        if (!hasEditor)
            return entries;

        var name = path.Length > 0 ? Path.GetFileName(path) : "エディタの内容";
        entries.Add(new(
            SelectionCompareKind.EditorWithClipboard,
            "ファイル全体とクリップボードを比較",
            "エディタの内容を左、クリップボードの内容を右に置いて Diff ペインで見比べる",
            name));
        if (path.Length > 0 && isModified && File.Exists(path))
            entries.Add(new(
                SelectionCompareKind.SavedEditorWithBuffer,
                "保存済みの内容と比較（未保存の変更）",
                "ディスク上の保存済みの内容と、編集中の内容の差分を Diff ペインで見る",
                name));
        return entries;
    }

    public static string SearchQuery(string text)
    {
        var collapsed = Regex.Replace(text.Trim(), @"\s+", " ");
        return collapsed.Length > MaxSearchQueryLength
            ? collapsed[..MaxSearchQueryLength]
            : collapsed;
    }

    public static SelectionBrowserSearchTarget? BrowserSearchTarget(string text)
    {
        var query = SearchQuery(text);
        return string.IsNullOrWhiteSpace(query)
            ? null
            : new SelectionBrowserSearchTarget(
                query, "https://www.bing.com/search?q=" + Uri.EscapeDataString(query), $"検索: {query}");
    }

    private static string UrlHost(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host is { Length: > 0 } host
            ? host
            : "ブラウザ";
}
