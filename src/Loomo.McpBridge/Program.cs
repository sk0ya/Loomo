using System.Text;
using sk0ya.Loomo.McpBridge;

// 標準入出力は UTF-8（BOM なし）。標準出力には相手の約束どおりのもの以外を書かない。
var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

if (args.Length > 0 && args[0] == "hook")
{
    // Claude Code のフック（§45.3 P0a）。入力は 1 つの JSON、出力は無いか 1 つの JSON。何があっても 0 で終わる。
    try
    {
        using var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
        var payload = await stdin.ReadToEndAsync();
        // Stop は Loomo が動きを録り終えるまで待つ（§45.6）。それ以外は短く（プロンプトごとに乗る）。
        var isStop = System.Text.Json.Nodes.JsonNode.Parse(payload) is System.Text.Json.Nodes.JsonObject o
                     && (string?)o["hook_event_name"] == "Stop";
        var relay = new HookRelay(StdioBridge.DefaultEndpointFile, BridgeContext.Detect(),
            TimeSpan.FromSeconds(isStop ? 95 : 3));
        if (await relay.RelayAsync(payload, CancellationToken.None) is { } reply)
        {
            using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8);
            await stdout.WriteAsync(reply);
        }
    }
    catch (Exception)
    {
        // フックの失敗で claude を止めない。
    }
    return 0;
}

// MCP（stdio）：1 行＝1 メッセージ。
using var input = new StreamReader(Console.OpenStandardInput(), utf8);
using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { NewLine = "\n" };
using var bridge = new StdioBridge(StdioBridge.DefaultEndpointFile, BridgeContext.Detect());
await bridge.RunAsync(input, output, CancellationToken.None);
return 0;
