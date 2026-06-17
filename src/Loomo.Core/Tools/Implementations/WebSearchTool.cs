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
/// まず <b>構造抽出</b>（<see cref="BingExtractScript"/>）で「回答ボックス（まとめ・ライブスコア・順位表・
/// ニュース等）＋オーガニック上位結果（タイトル・実URL・スニペット）」だけを取り出して返す。関連検索・
/// 画像/動画/ショッピングのカルーセル・フッタといったノイズ（実測で可視テキストの約3割）は収集しない。
/// 過去に構造抽出を見送った理由（Bing 冒頭の「まとめ」が落ちる／リンクが <c>bing.com/ck/a?...</c> リダイレクトに
/// なる）は、回答ボックスノードを明示的に拾い、<c>u=</c> パラメータを base64url デコードすることで解消した。
/// 抽出が空（DOM 変更・回答ボックス/結果なし）のときだけ従来の可視テキスト方式へフォールバックし、
/// <see cref="CleanPageText"/> で明らかなクロム（Cookie 同意バナー・スキップリンク・検索タブのナビ・空行・
/// 検索語の反復・表示用パンくず URL・「…を表示」等の展開ボタン・連続重複行）を安全に削ってから切り詰める。
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
        SerpExtract? extract;
        string rawText = string.Empty;
        try
        {
            page = await _browser.NavigateAsync(url, ct);
            extract = await TryExtractAsync(ct);                 // まず構造抽出（まとめ＋上位結果）
            // Bing は結果を遅延注入することがあるため、空なら少し待って一度だけ再試行。
            if (extract is null || extract.IsEmpty)
            {
                await Task.Delay(700, ct);
                extract = await TryExtractAsync(ct);
            }
            if (extract is null || extract.IsEmpty)
                rawText = await _browser.GetVisibleTextAsync(ct) ?? string.Empty;  // それでも取れなければ生テキストへフォールバック
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return ToolResult.Error($"ブラウザ検索に失敗しました: {ex.Message}"); }

        var body = extract is { IsEmpty: false }
            ? FormatStructured(extract)                          // 構造抽出が取れた → まとめ＋上位K件だけ返す
            : CleanPageText(rawText, query);                     // フォールバック：行クロム除去した生テキスト

        var truncated = body.Length > MaxResultChars;
        if (truncated) body = body[..MaxResultChars];

        var sb = new StringBuilder();
        sb.AppendLine($"query: {query}");
        sb.AppendLine($"url: {page.Url}");
        sb.AppendLine($"title: {page.Title}");
        sb.AppendLine(extract is { IsEmpty: false } ? "--- results ---" : "--- page text ---");
        sb.Append(body);
        if (truncated) sb.AppendLine().Append($"（…{MaxResultChars}文字で切り詰め）");
        return ToolResult.Ok(sb.ToString());
    }

    /// <summary>上限：返すオーガニック結果件数、まとめ（回答ボックス）文字数、各スニペット文字数。
    /// 「文字数で頭切り」ではなく「構造（件数×スニペット長）」でトークンを縛るための値。</summary>
    private const int MaxResults = 6;
    private const int MaxAnswerChars = 1500;
    private const int MaxSnippetChars = 300;

    /// <summary>Bing SERP の構造抽出結果。<see cref="Answer"/> は回答ボックス（まとめ・スコア・順位表等）、
    /// <see cref="Results"/> はオーガニック上位結果（タイトル・実URL・スニペット）。</summary>
    private sealed record SerpExtract(string Answer, IReadOnlyList<SerpResult> Results)
    {
        public bool IsEmpty => string.IsNullOrWhiteSpace(Answer) && Results.Count == 0;
    }

    private sealed record SerpResult(string Title, string Url, string Snippet);

    /// <summary>ブラウザ上で Bing SERP の構造抽出 JS を走らせ、<see cref="SerpExtract"/> へ復元する。
    /// 抽出/パースに失敗したら null（呼び出し側で生テキストへフォールバック）。</summary>
    private async Task<SerpExtract?> TryExtractAsync(CancellationToken ct)
    {
        string json;
        try { json = await _browser.EvaluateScriptAsync(BingExtractScript, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var answer = root.TryGetProperty("answer", out var a) ? (a.GetString() ?? "") : "";
            var results = new List<SerpResult>();
            if (root.TryGetProperty("results", out var rs) && rs.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in rs.EnumerateArray())
                {
                    var title = r.TryGetProperty("title", out var t) ? (t.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    var ru = r.TryGetProperty("url", out var u) ? (u.GetString() ?? "") : "";
                    var sn = r.TryGetProperty("snippet", out var s) ? (s.GetString() ?? "") : "";
                    results.Add(new SerpResult(title.Trim(), ru.Trim(), sn.Trim()));
                }
            }
            return new SerpExtract(answer.Trim(), results);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>構造抽出を、まとめ＋番号付き上位結果の読みやすいテキストへ整形する。
    /// まとめ・スニペットはそれぞれ上限文字数で切り、結果は最大 <see cref="MaxResults"/> 件。</summary>
    private static string FormatStructured(SerpExtract e)
    {
        var sb = new StringBuilder();
        var answer = TrimAnswerTail(e.Answer);
        if (!string.IsNullOrWhiteSpace(answer))
        {
            var ans = answer.Length > MaxAnswerChars ? answer[..MaxAnswerChars] + "…" : answer;
            sb.AppendLine("[answer]");
            sb.AppendLine(ans);
        }
        var n = 0;
        foreach (var r in e.Results)
        {
            if (n >= MaxResults) break;
            n++;
            sb.AppendLine($"{n}. {r.Title}");
            if (!string.IsNullOrEmpty(r.Url)) sb.AppendLine($"   {r.Url}");
            if (!string.IsNullOrEmpty(r.Snippet))
            {
                var sn = r.Snippet.Length > MaxSnippetChars ? r.Snippet[..MaxSnippetChars] + "…" : r.Snippet;
                sb.AppendLine($"   {sn}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>回答ボックスの innerText には本文の後に隣接ウィジェット（動画カルーセル・「他の人はこちらも検索」・
    /// 「すべて表示／さらに表示」で展開するカード列）が連結されることがある。本文＝先頭は正しいので、
    /// これら「展開境界」マーカーの最初の出現以降を切り捨ててノイズだけ落とす（本文は壊さない）。</summary>
    private static readonly string[] AnswerTailMarkers = { "さらに表示", "すべて表示", "すべて閲覧", "YouTube視聴回数" };

    internal static string TrimAnswerTail(string answer)
    {
        if (string.IsNullOrEmpty(answer)) return answer ?? string.Empty;
        var cut = -1;
        foreach (var m in AnswerTailMarkers)
        {
            var i = answer.IndexOf(m, StringComparison.Ordinal);
            if (i >= 0 && (cut < 0 || i < cut)) cut = i;
        }
        return (cut < 0 ? answer : answer[..cut]).TrimEnd();
    }

    /// <summary>Bing SERP から「回答ボックス（まとめ・スコア・順位表）」と「オーガニック上位結果」だけを
    /// 構造抽出する JS。関連検索・画像/動画/ショッピングのカルーセル・フッタ等のノイズは収集しない。
    /// 結果リンクは Bing の <c>/ck/a?...&u=a1&lt;base64url&gt;</c> リダイレクトを実URLへデコードする
    /// （デコード不能時は表示用 cite を、それも無ければ素の href を使う）。<c>JSON.stringify</c> で返す。</summary>
    private const string BingExtractScript = """
(function(){
  function clean(t){ return (t||'').replace(/\s+/g,' ').trim(); }
  function decodeUrl(href, citeText){
    try{
      if(!href) return clean(citeText)||'';
      var u=new URL(href, location.href);
      if(/(^|\.)bing\.com$/.test(u.hostname) && u.pathname.indexOf('/ck/')===0){
        var p=u.searchParams.get('u');
        if(p){
          var b=p.replace(/^a1/,'').replace(/-/g,'+').replace(/_/g,'/');
          try{ return decodeURIComponent(escape(atob(b))); }
          catch(e){ try{ return atob(b); }catch(e2){ return clean(citeText)||href; } }
        }
        return clean(citeText)||href;
      }
      return u.href;
    }catch(e){ return clean(citeText)||href||''; }
  }
  var out={answer:'', results:[]};
  // 回答ボックス：本文カラム上部の最初の回答ウィジェット（まとめ・スコア・順位表・ナレッジ等）。
  var ans=document.querySelector('#b_context li.b_ans, #b_results > li.b_ans, li.b_ans, .b_ans');
  if(ans){ out.answer=clean(ans.innerText).slice(0,2000); }
  // オーガニック結果：本文カラムの b_algo を上位から（直下に限らず子孫も拾う）。
  var items=document.querySelectorAll('#b_results li.b_algo, #b_results .b_algo, li.b_algo');
  for(var i=0;i<items.length && out.results.length<8;i++){
    var li=items[i];
    var h=li.querySelector('h2');
    var title=clean(h?h.innerText:'');
    if(!title) continue;
    var a=li.querySelector('h2 a')||li.querySelector('a');
    var cite=li.querySelector('cite');
    var capt=li.querySelector('.b_caption p')||li.querySelector('.b_algoSlug')||li.querySelector('p');
    out.results.push({
      title:title,
      url:decodeUrl(a?a.getAttribute('href'):'', cite?cite.innerText:''),
      snippet:clean(capt?capt.innerText:'').slice(0,400)
    });
  }
  return JSON.stringify(out);
})()
""";

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
