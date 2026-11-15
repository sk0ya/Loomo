using System.Text;
using sk0ya.Loomo.McpBridge;

// 標準入出力は UTF-8（BOM なし）・1 行＝1 メッセージ（MCP の stdio の約束）。標準出力にはメッセージ以外を書かない。
var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
using var input = new StreamReader(Console.OpenStandardInput(), utf8);
using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { NewLine = "\n" };

// Loomo のターミナルの中かどうかは、タブの ID で見る（MCP の口より前からある変数なので、古いシェルも持っている）。
var insideLoomo = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LOOMO_TERMINAL_TAB"));
using var bridge = new StdioBridge(StdioBridge.DefaultEndpointFile, insideLoomo);
await bridge.RunAsync(input, output, CancellationToken.None);
