using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Core.Tools.Implementations;

/// <summary>PowerShell（pwsh）でコマンドを実行する唯一のエージェントツール。
/// 読み取り・検索・一覧・作成・編集も全てこのツール経由の PowerShell コマンドで行う。</summary>
public sealed class PwshTool : IAgentTool
{
    private readonly ITerminalService _terminal;
    public PwshTool(ITerminalService terminal) => _terminal = terminal;

    /// <summary>command を表す引数キーの揺れ。小モデルは cmd/script/code 等で送ることがあるため別名で吸収する。</summary>
    private static readonly string[] CommandKeys =
        { "command", "cmd", "commandline", "command_line", "script", "code", "powershell", "pwsh", "input", "line" };

    public string Name => "pwsh";
    public bool RequiresApproval => true;   // コマンド実行なので承認必須

    public ToolDefinition Definition => new(
        Name,
        // 「ファイル操作も全て pwsh」はシステムプロンプト側に集約済みなので説明文では重複させない（プレフィル削減）。
        "PowerShell コマンドを実行し、標準出力と終了コードを返す。",
        ToolDefinition.ObjectSchema(
            // 例示値を添えるとスキーマ層でも具体像が伝わり、小モデルの空引数呼び出しが減る。
            ("command", "string", "実行する PowerShell コマンド行（例: Get-ChildItem）。空文字は不可。", true)));

    public string DescribeInvocation(JsonElement args) => $"$ {args.GetString("command")}";

    /// <summary>command の別名キーを吸収し、取れなければ唯一の string プロパティを採用して
    /// canonical な <c>{"command":"..."}</c> へ寄せる。安全評価・要約・実行が同じ値を見る。</summary>
    public JsonElement NormalizeArguments(JsonElement arguments)
    {
        var command = arguments.GetStringAny(CommandKeys);
        if (string.IsNullOrWhiteSpace(command))
            command = arguments.SingleStringValue();
        return JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["command"] = command });
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var command = args.GetString("command");
        // 空引数で呼ばれた場合は、再試行ターンが成功するよう正しい呼び出し形をエラー本文で示す。
        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Error("command が空です。arguments に {\"command\":\"<PowerShellコマンド>\"} を入れて呼び出してください。");

        var result = await _terminal.RunCommandAsync(command, ct);
        var sb = new StringBuilder();
        sb.AppendLine($"exit_code: {result.ExitCode}");
        sb.AppendLine($"cwd: {result.WorkingDirectory}");
        sb.AppendLine("--- output ---");
        sb.Append(result.Output);
        return result.Success ? ToolResult.Ok(sb.ToString()) : new ToolResult(sb.ToString(), IsError: true);
    }
}
