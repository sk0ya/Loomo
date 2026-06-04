using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Core.Tools.Implementations;

/// <summary>可視ブラウザペインのアクティブタブを指定URLへ遷移させる。</summary>
public sealed class BrowserNavigateTool : IAgentTool
{
    private readonly IBrowserService _browser;
    public BrowserNavigateTool(IBrowserService browser) => _browser = browser;

    public string Name => "browser_navigate";
    public bool RequiresApproval => true;   // 外部ネットワークへアクセスするため承認必須

    public ToolDefinition Definition => new(
        Name,
        "ブラウザペインのアクティブタブを指定URLへ遷移させ、読み込み完了後のURLとタイトルを返す。",
        ToolDefinition.ObjectSchema(
            ("url", "string", "遷移先URL。スキーム省略時は https を補う。", true)));

    public string DescribeInvocation(JsonElement args) => $"ブラウザ移動: {args.GetString("url")}";

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var url = args.GetString("url");
        if (string.IsNullOrWhiteSpace(url)) return ToolResult.Error("url は必須です。");
        if (!_browser.IsAvailable) return ToolResult.Error("ブラウザペインに操作可能なタブがありません。");

        try
        {
            var info = await _browser.NavigateAsync(url, ct);
            return ToolResult.Ok($"url: {info.Url}\ntitle: {info.Title}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException)
        {
            return ToolResult.Error(ex.Message);
        }
    }
}

/// <summary>現在表示中ページの可視テキストを抽出する。</summary>
public sealed class BrowserReadPageTool : IAgentTool
{
    private const int MaxChars = 20_000;

    private readonly IBrowserService _browser;
    public BrowserReadPageTool(IBrowserService browser) => _browser = browser;

    public string Name => "browser_read_page";
    public bool RequiresApproval => false;

    public ToolDefinition Definition => new(
        Name,
        "ブラウザで現在表示中のページの可視テキストを抽出して返す（先頭2万文字まで）。",
        ToolDefinition.ObjectSchema());

    public string DescribeInvocation(JsonElement args) => "ブラウザ読取";

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (!_browser.IsAvailable) return ToolResult.Error("ブラウザペインに操作可能なタブがありません。");

        try
        {
            var info = await _browser.GetPageInfoAsync(ct);
            var text = await _browser.GetVisibleTextAsync(ct);
            var truncated = text.Length > MaxChars;
            if (truncated) text = text[..MaxChars];
            var header = $"url: {info.Url}\ntitle: {info.Title}\n--- text ---\n";
            var footer = truncated ? "\n…(以下省略)" : string.Empty;
            return ToolResult.Ok(header + text + footer);
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Error(ex.Message);
        }
    }
}

/// <summary>現在表示中ページのURL・タイトルを返す。</summary>
public sealed class BrowserCurrentUrlTool : IAgentTool
{
    private readonly IBrowserService _browser;
    public BrowserCurrentUrlTool(IBrowserService browser) => _browser = browser;

    public string Name => "browser_current_url";
    public bool RequiresApproval => false;

    public ToolDefinition Definition => new(
        Name,
        "ブラウザで現在表示中ページのURLとタイトルを返す。",
        ToolDefinition.ObjectSchema());

    public string DescribeInvocation(JsonElement args) => "ブラウザURL取得";

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (!_browser.IsAvailable) return ToolResult.Error("ブラウザペインに操作可能なタブがありません。");

        try
        {
            var info = await _browser.GetPageInfoAsync(ct);
            return ToolResult.Ok($"url: {info.Url}\ntitle: {info.Title}");
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Error(ex.Message);
        }
    }
}

/// <summary>現在のページのクリック/入力可能な要素を一覧する。</summary>
public sealed class BrowserListClickablesTool : IAgentTool
{
    private const int MaxLines = 100;

    private readonly IBrowserService _browser;
    public BrowserListClickablesTool(IBrowserService browser) => _browser = browser;

    public string Name => "browser_list_clickables";
    public bool RequiresApproval => false;

    public ToolDefinition Definition => new(
        Name,
        "現在のページでクリック/入力できる要素（リンク・ボタン・入力欄）を、表示文言と CSS セレクタ付きで一覧する。"
        + "ここで得たセレクタをそのまま browser_click / browser_type に渡す。セレクタを推測しないこと。",
        ToolDefinition.ObjectSchema());

    public string DescribeInvocation(JsonElement args) => "ブラウザ要素一覧";

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (!_browser.IsAvailable) return ToolResult.Error("ブラウザペインに操作可能なタブがありません。");

        try
        {
            var items = await _browser.ListClickablesAsync(ct);
            if (items.Count == 0) return ToolResult.Ok("(クリック/入力できる要素が見つかりません)");

            var sb = new System.Text.StringBuilder();
            foreach (var it in items.Take(MaxLines))
            {
                var text = string.IsNullOrEmpty(it.Text) ? "(文言なし)" : it.Text;
                sb.Append('<').Append(it.Tag).Append("> \"").Append(text).Append("\" → ").Append(it.Selector).Append('\n');
            }

            return ToolResult.Ok(sb.ToString());
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Error(ex.Message);
        }
    }
}

/// <summary>CSSセレクタに一致する要素をクリックする。</summary>
public sealed class BrowserClickTool : IAgentTool
{
    private readonly IBrowserService _browser;
    public BrowserClickTool(IBrowserService browser) => _browser = browser;

    public string Name => "browser_click";
    public bool RequiresApproval => true;   // ページ操作（外部への副作用）なので承認必須

    public ToolDefinition Definition => new(
        Name,
        "現在のページでCSSセレクタに一致する最初の要素をクリックする。"
        + "セレクタは推測せず、browser_list_clickables が返したものを使う。",
        ToolDefinition.ObjectSchema(
            ("selector", "string", "クリック対象のCSSセレクタ。browser_list_clickables の出力をそのまま使う。", true)));

    public string DescribeInvocation(JsonElement args) => $"ブラウザクリック: {args.GetString("selector")}";

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var selector = args.GetString("selector");
        if (string.IsNullOrWhiteSpace(selector)) return ToolResult.Error("selector は必須です。");
        if (!_browser.IsAvailable) return ToolResult.Error("ブラウザペインに操作可能なタブがありません。");

        try
        {
            await _browser.ClickAsync(selector, ct);
            return ToolResult.Ok($"クリックしました: {selector}");
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Error(ex.Message);
        }
    }
}

/// <summary>CSSセレクタに一致する入力要素へテキストを入力する。</summary>
public sealed class BrowserTypeTool : IAgentTool
{
    private readonly IBrowserService _browser;
    public BrowserTypeTool(IBrowserService browser) => _browser = browser;

    public string Name => "browser_type";
    public bool RequiresApproval => true;   // フォーム入力（外部への副作用）なので承認必須

    public ToolDefinition Definition => new(
        Name,
        "現在のページでCSSセレクタに一致する入力要素へテキストを設定し、input/change を発火する。"
        + "セレクタは推測せず、browser_list_clickables が返したものを使う。",
        ToolDefinition.ObjectSchema(
            ("selector", "string", "入力対象のCSSセレクタ。browser_list_clickables の出力をそのまま使う。", true),
            ("text", "string", "設定するテキスト。", true)));

    public string DescribeInvocation(JsonElement args)
        => $"ブラウザ入力: {args.GetString("selector")} ← \"{args.GetString("text")}\"";

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var selector = args.GetString("selector");
        if (string.IsNullOrWhiteSpace(selector)) return ToolResult.Error("selector は必須です。");
        var text = args.GetString("text");
        if (!_browser.IsAvailable) return ToolResult.Error("ブラウザペインに操作可能なタブがありません。");

        try
        {
            await _browser.TypeAsync(selector, text, ct);
            return ToolResult.Ok($"入力しました: {selector}");
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Error(ex.Message);
        }
    }
}

/// <summary>現在のページのスクリーンショットをPNGで保存し、保存先パスを返す。</summary>
public sealed class BrowserScreenshotTool : IAgentTool
{
    private readonly IBrowserService _browser;
    public BrowserScreenshotTool(IBrowserService browser) => _browser = browser;

    public string Name => "browser_screenshot";
    public bool RequiresApproval => false;

    public ToolDefinition Definition => new(
        Name,
        "現在のページのスクリーンショット(PNG)を一時フォルダへ保存し、保存先の絶対パスを返す。",
        ToolDefinition.ObjectSchema());

    public string DescribeInvocation(JsonElement args) => "ブラウザ撮影";

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (!_browser.IsAvailable) return ToolResult.Error("ブラウザペインに操作可能なタブがありません。");

        try
        {
            var png = await _browser.CaptureScreenshotAsync(ct);
            var dir = Path.Combine(Path.GetTempPath(), "Loomo", "screenshots");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"browser-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            await File.WriteAllBytesAsync(path, png, ct);
            PruneOldScreenshots(dir);
            return ToolResult.Ok($"スクリーンショットを保存しました: {path}");
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Error(ex.Message);
        }
    }

    /// <summary>一時フォルダの無制限な肥大化を防ぐため、最新 <see cref="KeepFiles"/> 件だけ残して古い物を消す。</summary>
    private static void PruneOldScreenshots(string dir)
    {
        try
        {
            var files = new DirectoryInfo(dir).GetFiles("browser-*.png");
            if (files.Length <= KeepFiles) return;
            foreach (var file in files.OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeepFiles))
            {
                try { file.Delete(); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private const int KeepFiles = 50;
}
