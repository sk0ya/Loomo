using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Tools.Implementations;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>web_search の加工（可視テキストの返却・ヘッダ付与・切り詰め・前提エラー）を検証する。
/// 実 DOM/ネットワーク挙動は WebView2 上でしか確認できないため、ここでは可視テキストを差し込める
/// fake ブラウザで C# 側のふるまいを通す。冒頭のまとめを残すため生テキストをそのまま返す方針
/// （構造抽出は Bing のまとめ欠落・リダイレクト URL 化で生テキストに劣ると実機検証で判明したため不採用）。</summary>
public class WebSearchToolTests
{
    private sealed class FakeBrowser : IBrowserService
    {
        public bool Available = true;
        public string VisibleText = "";
        public string ScriptResult = "";     // EvaluateScriptAsync の戻り。空なら構造抽出なし＝生テキストへフォールバック。
        public string? NavigatedUrl;

        public bool IsAvailable => Available;
        public Task<BrowserPageInfo> NavigateAsync(string url, CancellationToken ct)
        { NavigatedUrl = url; return Task.FromResult(new BrowserPageInfo(url, "検索 - Bing")); }
        public Task<BrowserPageInfo> GetPageInfoAsync(CancellationToken ct)
            => Task.FromResult(new BrowserPageInfo(NavigatedUrl ?? "", ""));
        public Task<IReadOnlyList<BrowserClickable>> ListClickablesAsync(CancellationToken ct)
            => Task.FromResult((IReadOnlyList<BrowserClickable>)Array.Empty<BrowserClickable>());
        public Task<string> GetVisibleTextAsync(CancellationToken ct) => Task.FromResult(VisibleText);
        public Task<string> EvaluateScriptAsync(string script, CancellationToken ct) => Task.FromResult(ScriptResult);
        public Task ClickAsync(string selector, CancellationToken ct) => Task.CompletedTask;
        public Task TypeAsync(string selector, string text, CancellationToken ct) => Task.CompletedTask;
        public Task<byte[]> CaptureScreenshotAsync(CancellationToken ct) => Task.FromResult(Array.Empty<byte>());
    }

    private static JsonElement Args(string query)
        => JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["query"] = query });

    [Fact]
    public async Task Returns_visible_text_with_header()
    {
        var browser = new FakeBrowser { VisibleText = "SUMMARY_BLOCK 冒頭のまとめ\nRESULT_BODY 以降の本文" };
        var result = await new WebSearchTool(browser).ExecuteAsync(Args("サンプル検索語"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("query: サンプル検索語", result.Content);
        Assert.Contains("--- page text ---", result.Content);
        // 冒頭のまとめがそのまま含まれている（構造抽出で落とさない）。
        Assert.Contains("SUMMARY_BLOCK 冒頭のまとめ", result.Content);
        // query をエスケープしてナビゲート URL を組んでいる（生のスペースを含まない）。
        Assert.StartsWith("https://www.bing.com/search?q=", browser.NavigatedUrl);
        Assert.DoesNotContain("サンプル検索語", browser.NavigatedUrl);   // 生のままではなくエスケープ済み
    }

    [Fact]
    public async Task Strips_obvious_chrome_but_keeps_summary_and_results()
    {
        var raw = string.Join("\n",
            "このサイトを利用すると、分析、広告に Cookie を使用することに同意したことになります。",
            "コンテンツに移動",
            "アクセシビリティに関するフィードバック",
            "サンプル検索語",                              // 検索ボックスのクエリ反復
            "Rewards",
            "すべて検索画像動画地図ニュースCOPILOT",        // 検索タブのナビ行
            "",                                            // 空行
            "SUMMARY_BLOCK 冒頭のまとめ本文",               // ← 残すべきまとめ
            "RESULT_BODY 検索結果の本文");                  // ← 残すべき結果
        var browser = new FakeBrowser { VisibleText = raw };

        var result = await new WebSearchTool(browser).ExecuteAsync(Args("サンプル検索語"), CancellationToken.None);

        // クロムは削られている。
        Assert.DoesNotContain("このサイトを利用すると", result.Content);
        Assert.DoesNotContain("コンテンツに移動", result.Content);
        Assert.DoesNotContain("アクセシビリティに関するフィードバック", result.Content);
        Assert.DoesNotContain("Rewards", result.Content);
        Assert.DoesNotContain("すべて検索画像動画地図ニュースCOPILOT", result.Content);
        // まとめ・結果は残っている。
        Assert.Contains("SUMMARY_BLOCK 冒頭のまとめ本文", result.Content);
        Assert.Contains("RESULT_BODY 検索結果の本文", result.Content);
    }

    [Fact]
    public void CleanPageText_drops_blank_lines_and_query_echo()
    {
        var cleaned = WebSearchTool.CleanPageText("  \n結果1\n\n  \nクエリ\n結果2\n", "クエリ");
        Assert.Equal("結果1\n結果2", cleaned);
    }

    [Fact]
    public void CleanPageText_drops_breadcrumb_urls_more_buttons_and_adjacent_dupes()
    {
        var raw = string.Join("\n",
            "サイト名",
            "https://example.com › ja › news",   // 表示用パンくず URL
            "結果の本文",
            "その他の試合を表示",                  // 展開ボタン
            "すべての順位表を表示",                // 展開ボタン
            "重複行",
            "重複行",                              // 直前と同一 → 1つに
            "JST で表示");                         // 「を表示」で終わらない注記 → 残す
        var cleaned = WebSearchTool.CleanPageText(raw, "q");

        Assert.Equal(
            string.Join("\n", "サイト名", "結果の本文", "重複行", "JST で表示"),
            cleaned);
    }

    [Fact]
    public async Task Truncates_long_text()
    {
        var browser = new FakeBrowser { VisibleText = new string('あ', 6000) };
        var result = await new WebSearchTool(browser).ExecuteAsync(Args("x"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("文字で切り詰め", result.Content);
        Assert.DoesNotContain(new string('あ', 5000), result.Content);   // 6000 字フルでは含まれない
    }

    [Fact]
    public async Task Structured_extraction_returns_answer_and_numbered_results()
    {
        // 構造抽出 JS が返す JSON を差し込む。これがあれば生テキストではなく構造化結果を返す。
        var json = """
        {"answer":"まとめ本文です。","results":[
          {"title":"結果A","url":"https://a.example.com/page","snippet":"スニペットA"},
          {"title":"結果B","url":"https://b.example.com/","snippet":"スニペットB"}]}
        """;
        var browser = new FakeBrowser { ScriptResult = json, VisibleText = "FALLBACK_SHOULD_NOT_APPEAR" };
        var result = await new WebSearchTool(browser).ExecuteAsync(Args("q"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("--- results ---", result.Content);
        Assert.Contains("[answer]", result.Content);
        Assert.Contains("まとめ本文です。", result.Content);
        Assert.Contains("1. 結果A", result.Content);
        Assert.Contains("https://a.example.com/page", result.Content);
        Assert.Contains("2. 結果B", result.Content);
        // 構造抽出が取れたら生テキストのフォールバックは使わない。
        Assert.DoesNotContain("FALLBACK_SHOULD_NOT_APPEAR", result.Content);
        Assert.DoesNotContain("--- page text ---", result.Content);
    }

    [Fact]
    public void TrimAnswerTail_cuts_at_show_more_and_video_markers()
    {
        // 「すべて閲覧」以降（展開カード列）は落とし、本文は残す。
        Assert.Equal("本文です。", WebSearchTool.TrimAnswerTail("本文です。 すべて閲覧 関連カード1 関連カード2"));
        // 動画カルーセル（YouTube視聴回数）マーカー以降も落とす。
        Assert.Equal("回答の本体。", WebSearchTool.TrimAnswerTail("回答の本体。 YouTube視聴回数: 819 回 関連ニュース"));
        // 複数マーカーがあるときは最初の出現で切る。
        Assert.Equal("先頭。", WebSearchTool.TrimAnswerTail("先頭。 さらに表示 中間 すべて表示 末尾"));
        // マーカーが無ければそのまま。
        Assert.Equal("マーカー無しの回答", WebSearchTool.TrimAnswerTail("マーカー無しの回答"));
    }

    [Fact]
    public async Task Falls_back_to_raw_text_when_extraction_empty()
    {
        // 抽出が空（results 無し・answer 無し）→ 生テキスト方式へフォールバック。
        var browser = new FakeBrowser
        {
            ScriptResult = """{"answer":"","results":[]}""",
            VisibleText = "生テキストのまとめ\n結果の本文",
        };
        var result = await new WebSearchTool(browser).ExecuteAsync(Args("q"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("--- page text ---", result.Content);
        Assert.Contains("生テキストのまとめ", result.Content);
    }

    [Fact]
    public async Task Errors_when_browser_unavailable()
    {
        var result = await new WebSearchTool(new FakeBrowser { Available = false })
            .ExecuteAsync(Args("x"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("ブラウザペイン", result.Content);
    }

    [Fact]
    public async Task Errors_on_empty_query()
    {
        var result = await new WebSearchTool(new FakeBrowser()).ExecuteAsync(Args(""), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("query", result.Content);
    }
}
