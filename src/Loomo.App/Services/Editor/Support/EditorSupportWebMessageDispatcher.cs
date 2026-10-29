namespace sk0ya.Loomo.App.Services;

/// <summary>EditorSupport ページから届く postMessage を操作へ振り分ける。</summary>
internal sealed class EditorSupportWebMessageDispatcher
{
    private readonly Action<CoreWebView2, JsonElement, string?> _pochiBridge;
    private readonly Action<EditorSupportPageScroll> _scrollSource;
    private readonly Action<int?> _jumpToSource;
    private readonly Action<string> _openLink;
    private readonly Action<int> _toggleTaskCheckbox;
    private readonly Action<string> _applyMarkdownEdit;

    public EditorSupportWebMessageDispatcher(
        Action<CoreWebView2, JsonElement, string?> pochiBridge,
        Action<EditorSupportPageScroll> scrollSource,
        Action<int?> jumpToSource,
        Action<string> openLink,
        Action<int> toggleTaskCheckbox,
        Action<string> applyMarkdownEdit)
    {
        _pochiBridge = pochiBridge;
        _scrollSource = scrollSource;
        _jumpToSource = jumpToSource;
        _openLink = openLink;
        _toggleTaskCheckbox = toggleTaskCheckbox;
        _applyMarkdownEdit = applyMarkdownEdit;
    }

    public void Dispatch(string json, object? sender, bool sourceAvailable)
    {
        if (!sourceAvailable)
            return;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("op", out var opElement) && sender is CoreWebView2 bridgeCore)
            {
                _pochiBridge(bridgeCore, root, opElement.GetString());
                return;
            }
            if (!root.TryGetProperty("type", out var typeElement))
                return;

            switch (typeElement.GetString())
            {
                case "markdownPreviewScroll":
                    if (root.TryGetProperty("ratio", out var ratioElement)
                        && ratioElement.TryGetDouble(out var ratio))
                        _scrollSource(new EditorSupportPageScroll(
                            ratio,
                            root.TryGetProperty("line", out var scrollLineElement)
                                && scrollLineElement.TryGetDouble(out var scrollLine) ? scrollLine : null,
                            root.TryGetProperty("tail", out var tailElement)
                                && tailElement.TryGetDouble(out var tail) ? tail : 0));
                    break;
                case "jumpToSource":
                    var line = root.TryGetProperty("line", out var lineElement)
                        && lineElement.TryGetInt32(out var lineNumber) ? lineNumber : 0;
                    _jumpToSource(line > 0 ? line : null);
                    break;
                case "linkClicked":
                    if (root.TryGetProperty("href", out var hrefElement)
                        && hrefElement.GetString() is { } href)
                        _openLink(href);
                    break;
                case "toggleTaskCheckbox":
                    if (root.TryGetProperty("line", out var taskLineElement)
                        && taskLineElement.TryGetInt32(out var taskLine))
                        _toggleTaskCheckbox(taskLine);
                    break;
                case "markdownEdited":
                    if (root.TryGetProperty("text", out var markdownTextElement)
                        && markdownTextElement.GetString() is { } markdownText)
                        _applyMarkdownEdit(markdownText);
                    break;
            }
        }
        catch
        {
            // 読み込み途中のページや別のWebコンテンツからの不正メッセージは捨てる。
        }
    }
}

/// <summary>ページ側のスクロール位置。<see cref="Line"/> は先頭に見えているソース行（小数）で、
/// 行の目印を持たないページでは null（<see cref="Ratio"/> で合わせる）。<see cref="Tail"/> は末尾1画面での
/// 終端への寄せ（<see cref="EditorSupportScrollSync.TailBlend"/>）。</summary>
internal readonly record struct EditorSupportPageScroll(double Ratio, double? Line, double Tail);
