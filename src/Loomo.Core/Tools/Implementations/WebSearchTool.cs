using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Core.Tools.Implementations;

/// <summary>
/// 可視ブラウザペインでウェブ検索する構造化ツール。検索語を <c>query</c> 引数で受け取り、
/// <see cref="IBrowserService"/>（人間が見ているブラウザペインのアクティブタブへ一本化）で
/// 検索エンジンの結果ページへ遷移し、可視テキストを抽出して返す。
/// <see cref="ITerminalService"/>/<see cref="IEditorService"/> と同じく「AIは可視ペインを操作する」思想に従い、
/// 別ウィンドウは起動しない（ブラウザペインに開いたタブがそのまま操作対象になる）。
///
/// 返すのはページの可視テキスト（document.body.innerText）。結果項目だけを構造抽出する案も試したが、
/// Bing が冒頭に置く「まとめ」（ライブスコア・順位表・ニュース等の回答ボックス）が落ち、リンクも
/// リダイレクト追跡 URL（bing.com/ck/a?...）になって実 URL が失われたため、生テキストの方が情報量・
/// 先頭のまとめ・実ドメイン表示の点で小モデルにとって有用だった（実機検証で確認）。
/// ただし明らかなクロム（Cookie 同意バナー・スキップリンク・検索タブのナビ・空行・検索語の反復・
/// 表示用パンくず URL・「…を表示」等の展開ボタン・連続重複行）は <see cref="CleanPageText"/> で安全に削り、
/// トークンを節約してから切り詰める（まとめ・結果は壊さない）。
/// </summary>
public sealed class WebSearchTool : IAgentTool
{
    private readonly IBrowserService _browser;

    public WebSearchTool(IBrowserService browser) => _browser = browser;

    /// <summary>検索結果テキストの返却上限（文字数）。小モデルのコンテキストを膨らませないため切り詰める。
    /// 冒頭のまとめ＋上位結果が収まる程度に抑える（NumCtx は意図的に小さい）。</summary>
    private const int MaxResultChars = 4000;

    public string Name => WebSearchContract.ToolName;
    public bool RequiresApproval => true;   // 外部ネットワークへの遷移なので承認必須

    public ToolDefinition Definition => new(
        Name,
        "Search the web in the browser pane and return the result page text. Use it to look up external information not in the workspace.",
        ToolDefinition.ObjectSchema(
            (WebSearchContract.QueryArg, "string", "Search query, e.g. .NET 9 release notes.", true)));

    public string DescribeInvocation(JsonElement args) => $"web_search: {args.GetString(WebSearchContract.QueryArg)}";

    /// <summary>query の別名キーを吸収し、取れなければ唯一の string プロパティを採用して
    /// canonical な <c>{"query":"..."}</c> へ寄せる。安全評価・要約・実行が同じ値を見る。</summary>
    public JsonElement NormalizeArguments(JsonElement arguments)
    {
        var query = arguments.GetStringAny(WebSearchContract.QueryKeys);
        if (string.IsNullOrWhiteSpace(query))
            query = arguments.SingleStringValue();
        return JsonSerializer.SerializeToElement(
            new Dictionary<string, string> { [WebSearchContract.QueryArg] = query });
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var query = args.GetString(WebSearchContract.QueryArg);
        if (string.IsNullOrWhiteSpace(query))
            return ToolResult.Error("query が空です。arguments に {\"query\":\"<検索語>\"} を入れて呼び出してください。");

        // 操作対象は可視ブラウザペインのアクティブタブ。未実体化（ペイン未表示など）なら honest に差し戻す。
        if (!_browser.IsAvailable)
            return ToolResult.Error(
                "ブラウザペインにアクティブなタブがありません。ブラウザペインを開いて（タブを1つ表示して）から再実行してください。");

        var url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(query);

        BrowserPageInfo page;
        string text;
        try
        {
            page = await _browser.NavigateAsync(url, ct);
            text = await _browser.GetVisibleTextAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return ToolResult.Error($"ブラウザ検索に失敗しました: {ex.Message}"); }

        text = CleanPageText(text ?? string.Empty, query);
        var truncated = text.Length > MaxResultChars;
        if (truncated) text = text[..MaxResultChars];

        var sb = new StringBuilder();
        sb.AppendLine($"query: {query}");
        sb.AppendLine($"url: {page.Url}");
        sb.AppendLine($"title: {page.Title}");
        sb.AppendLine("--- page text ---");
        sb.Append(text);
        if (truncated) sb.AppendLine().Append($"（…{MaxResultChars}文字で切り詰め）");
        return ToolResult.Ok(sb.ToString());
    }

    /// <summary>SERP の可視テキスト中、各行と完全一致で除外する明らかなクロム（JP/EN）。
    /// 部分一致にすると結果文を巻き込むため、トリム後の<b>行全体一致</b>のみ削る。</summary>
    private static readonly string[] ChromeLines =
    {
        "コンテンツに移動", "メイン コンテンツにスキップ", "メインコンテンツにスキップ",
        "アクセシビリティに関するフィードバック", "Rewards", "さらに表示",
        "Skip to content", "Skip to main content",
    };

    /// <summary>
    /// 可視テキストから、まとめ・結果を保ったまま削れる文字だけを安全に削る：
    /// 行頭・行末の空白を整え、空行・Cookie 同意バナー・スキップリンク・検索タブのナビ行・検索語の反復行・
    /// 表示用パンくず URL 行（<c>›</c> 区切り）・「…を表示」等の展開ボタン・直前行と同一の重複行を除く。
    /// 再構成や要約はしない（冒頭のまとめを壊さないため）。判定はすべてトリム後の<b>行単位</b>で、
    /// スコアや順位の実データ・結果スニペットを巻き込まない保守的なものに限る。
    /// </summary>
    internal static string CleanPageText(string raw, string query)
    {
        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var l in lines)
        {
            var line = l.Trim();
            if (line.Length == 0) continue;                                   // 空行
            if (line == query.Trim()) continue;                              // 検索ボックスのクエリ反復
            if (line.StartsWith("このサイトを利用すると", StringComparison.Ordinal)) continue;   // Cookie 同意バナー
            if (IsChromeLine(line)) continue;                                // スキップリンク等の定型クロム
            if (IsSearchTabBar(line)) continue;                              // 「すべて検索画像動画地図ニュース…」のタブ行
            if (line.Contains('›')) continue;                                // Bing の表示用パンくず URL（実 URL ではない）
            if (IsMoreButton(line)) continue;                                // 「その他の試合を表示」等の展開ボタン
            if (kept.Count > 0 && kept[^1] == line) continue;                // 直前と同一の重複行
            kept.Add(line);
        }
        return string.Join('\n', kept);
    }

    private static bool IsChromeLine(string line)
    {
        foreach (var c in ChromeLines)
            if (string.Equals(line, c, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>SERP 上部の検索カテゴリのタブ行（例「すべて検索画像動画地図ニュースCOPILOT」）を判定する。
    /// 短い行に画像・動画・ニュースが揃うのはタブ行に固有のため、長い結果文は巻き込まない。</summary>
    private static bool IsSearchTabBar(string line)
        => line.Length <= 40 && line.Contains("画像") && line.Contains("動画") && line.Contains("ニュース");

    /// <summary>「その他の試合を表示」「すべての順位表を表示」等の展開（show more）ボタン行を判定する。
    /// 「…を表示」で終わる短い行に限る（「JST で表示」のような注記は <c>を表示</c> で終わらないので残る）。</summary>
    private static bool IsMoreButton(string line)
        => line.Length <= 15 && line.EndsWith("を表示", StringComparison.Ordinal);
}
