namespace sk0ya.Loomo.Core.Tools;

/// <summary>
/// ブラウザ検索ツール <c>web_search</c> の契約（ツール名・canonical な引数キー・キー別名）。
/// <see cref="PwshContract"/> と同じ流儀で、定義・正規化・実行が同じ語彙を共有する。
/// 検索語を <c>query</c> という独立 JSON 引数で受け、可視ブラウザペインで検索して結果テキストを返す。
/// </summary>
public static class WebSearchContract
{
    /// <summary>ツール名。</summary>
    public const string ToolName = "web_search";

    /// <summary>canonical な引数キー（正規化後は必ずこのキーに揃う）。</summary>
    public const string QueryArg = "query";

    /// <summary>query を表す引数キーの揺れ。小モデルは q/search/keyword 等で送ることがある。先頭が canonical。</summary>
    public static readonly string[] QueryKeys =
        { QueryArg, "q", "search", "keyword", "keywords", "text", "term", "terms", "input" };
}
