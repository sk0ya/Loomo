using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentStudio.Core.Abstractions;

namespace AgentStudio.Core.Tools.Implementations;

/// <summary>ターミナルでコマンドを実行する。</summary>
public sealed class RunCommandTool : IAgentTool
{
    private readonly ITerminalService _terminal;
    public RunCommandTool(ITerminalService terminal) => _terminal = terminal;

    public string Name => "run_command";
    public bool RequiresApproval => true;   // コマンド実行なので承認必須

    public ToolDefinition Definition => new(
        Name,
        "ワークスペースのターミナルでシェルコマンドを実行し、標準出力と終了コードを返す。",
        ToolDefinition.ObjectSchema(
            ("command", "string", "実行するコマンド行。", true)));

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
