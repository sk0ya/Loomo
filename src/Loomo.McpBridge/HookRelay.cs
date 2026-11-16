using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace sk0ya.Loomo.McpBridge;

/// <summary>
/// Claude Code のフック（<c>sk0ya.Loomo.McpBridge.exe hook</c>、§45.3 P0a）。前の PowerShell のスクリプトは名前付きパイプへ
/// 投げて返事を待たなかったが、これは Loomo の答え（フックの出力の JSON）を受けて標準出力へ書く——次の頼みごとに文脈を
/// 添える（§45.4）・ターンを続けさせる（§45.6）ための口。
/// <list type="bullet">
/// <item><b>Loomo の外では即終了</b>（何も書かず 0 で終わる）。設定はユーザー全体に入るので、外の claude では無害であること。</item>
/// <item><b>どの Loomo のどのタブか</b>は環境変数とプロセスの親子で名乗り、そのシェルを持つ Loomo だけが答える（<see cref="LoomoLink"/>）。</item>
/// <item><b>MCP サーバーが動いていない Loomo</b>（設定の Claude Code 連携を切ってある）には、前の版と同じ名前付きパイプで
/// 片道だけ送る（承認待ちのバッジとセッションの結び付けは、これまでどおり効く）。</item>
/// <item>何が起きても 0 で終わり、claude を止めない。待つのは上限まで。</item>
/// </list>
/// </summary>
internal sealed class HookRelay(string endpointFile, BridgeContext context, TimeSpan timeout)
{
    /// <summary>フックの入力を Loomo へ渡し、標準出力へ書くもの（無ければ null）を返す。</summary>
    public async Task<string?> RelayAsync(string payload, CancellationToken cancel)
    {
        if (!context.InsideLoomo || string.IsNullOrWhiteSpace(payload))
            return null;
        JsonObject? hookEvent;
        try { hookEvent = JsonNode.Parse(payload) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
        if (hookEvent is null)
            return null;

        using var link = new LoomoLink(endpointFile, context, timeout);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout);
        LoomoLink.Response? response = null;
        try { response = await link.PostAsync("/hook", payload, limit.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        if (response is not null)
            return response.Status == 200 && response.Body.Length > 0 ? response.Body : null;   // 421＝どのタブでもない：何もしない

        SendThroughLegacyPipe(hookEvent);
        return null;
    }

    /// <summary>前の版の通り道（<c>ClaudeHookBridge</c> のパイプ）。タブとパイプの名前は環境変数でしか分からない。</summary>
    private static void SendThroughLegacyPipe(JsonObject hookEvent)
    {
        var pipe = Environment.GetEnvironmentVariable("LOOMO_CLAUDE_HOOK_PIPE");
        var tab = Environment.GetEnvironmentVariable("LOOMO_TERMINAL_TAB");
        if (string.IsNullOrEmpty(pipe) || string.IsNullOrEmpty(tab))
            return;
        try
        {
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.Asynchronous);
            client.Connect(500);
            var line = new JsonObject { ["tab"] = tab, ["event"] = hookEvent.DeepClone() }.ToJsonString() + "\n";
            client.Write(Encoding.UTF8.GetBytes(line));
            client.Flush();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException) { }
    }
}
