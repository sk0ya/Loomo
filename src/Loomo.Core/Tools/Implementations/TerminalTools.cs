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

    public string Name => "pwsh";
    public bool RequiresApproval => true;   // コマンド実行なので承認必須

    public ToolDefinition Definition => new(
        Name,
        // 「ファイル操作も全て pwsh」はシステムプロンプト側に集約済みなので説明文では重複させない（プレフィル削減）。
        "PowerShell コマンドを実行し、標準出力と終了コードを返す。",
        ToolDefinition.ObjectSchema(
            ("command", "string", "実行する PowerShell コマンド行。", true)));

    public string DescribeInvocation(JsonElement args) => $"$ {args.GetString("command")}";

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var command = args.GetString("command");
        if (string.IsNullOrWhiteSpace(command)) return ToolResult.Error("command は必須です。");

        var result = await _terminal.RunCommandAsync(command, ct);
        var sb = new StringBuilder();
        sb.AppendLine($"exit_code: {result.ExitCode}");
        sb.AppendLine($"cwd: {result.WorkingDirectory}");
        sb.AppendLine("--- output ---");
        sb.Append(result.Output);
        return result.Success ? ToolResult.Ok(sb.ToString()) : new ToolResult(sb.ToString(), IsError: true);
    }
}
