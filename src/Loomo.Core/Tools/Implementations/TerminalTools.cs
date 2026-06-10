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

    public string Name => PwshContract.ToolName;
    public bool RequiresApproval => true;   // コマンド実行なので承認必須

    public ToolDefinition Definition => new(
        Name,
        // 「ファイル操作も全て PowerShell」はシステムプロンプト側に集約済みなので説明文では重複させない（プレフィル削減）。
        "Run one non-interactive PowerShell command and return stdout and exit code.",
        ToolDefinition.ObjectSchema(
            // 例示値を添えるとスキーマ層でも具体像が伝わり、小モデルの空引数呼び出しが減る。
            (PwshContract.CommandArg, "string", "Non-empty non-interactive command; avoid pagers and prompts.", true)));

    public string DescribeInvocation(JsonElement args) => $"$ {args.GetString(PwshContract.CommandArg)}";

    /// <summary>command の別名キーを吸収し、取れなければ唯一の string プロパティを採用して
    /// canonical な <c>{"command":"..."}</c> へ寄せる。安全評価・要約・実行が同じ値を見る。</summary>
    public JsonElement NormalizeArguments(JsonElement arguments)
    {
        var command = arguments.GetStringAny(PwshContract.CommandKeys);
        if (string.IsNullOrWhiteSpace(command))
            command = arguments.SingleStringValue();
        return JsonSerializer.SerializeToElement(
            new Dictionary<string, string> { [PwshContract.CommandArg] = command });
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var command = args.GetString(PwshContract.CommandArg);
        // 空引数で呼ばれた場合は、再試行ターンが成功するよう正しい呼び出し形をエラー本文で示す。
        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Error("command が空です。arguments に {\"command\":\"<PowerShellコマンド>\"} を入れて呼び出してください。");

        // 同一ファイルを読みながら同じパイプラインで書き戻すコマンドは、ファイル破壊（使用中エラー・
        // 内容消失）になるため実行前に差し戻し、安全な書き方を案内する（実行はしない）。
        var sameFile = PwshSameFilePipeGuard.DetectSameFileReadWrite(command);
        if (sameFile is not null)
            return ToolResult.Error(
                $"このコマンドは {sameFile} を Get-Content で読みながら同じパイプラインで書き込むため、"
                + "「別のプロセスで使用中」エラーやファイル内容の消失を起こします。実行していません。"
                + "$c = Get-Content <file> のように一度変数へ読み込んでから Set-Content <file> $c で"
                + "書き戻すか、行の置換・削除・追記なら edit_file を使ってください。");

        command = MakeNonInteractive(command);

        var result = await _terminal.RunCommandAsync(command, ct);
        var sb = new StringBuilder();
        sb.AppendLine($"exit_code: {result.ExitCode}");
        sb.AppendLine($"cwd: {result.WorkingDirectory}");
        sb.AppendLine("--- output ---");
        sb.Append(result.Output);
        if (result.Success) return ToolResult.Ok(sb.ToString());

        // 失敗時は既知の回復可能パターンに復旧手順を1行添える。小モデルはエラー文だけだと諦めて
        // ユーザーへ作業を投げ返す／失敗を「無かった」ことにするため、次の一手を機械的に示す。
        var hint = RecoveryHint(result.Output);
        if (hint is not null) sb.AppendLine().Append(hint);
        return new ToolResult(sb.ToString(), IsError: true);
    }

    /// <summary>失敗出力から既知の回復手順を判定する（無ければ null）。</summary>
    private static string? RecoveryHint(string output)
    {
        // 日本語メッセージは「パス '<実パス>' の一部が見つかりませんでした」形式（パスが間に挟まる）なので
        // 後半だけで照合する。コンソール幅の折返しで語中に改行が入ることがあるため、語尾は含めない。
        if (output.Contains("の一部が見つかりません") ||
            output.Contains("Could not find a part of the path", System.StringComparison.OrdinalIgnoreCase))
            return "ヒント: 途中のフォルダが存在しない可能性があります。"
                   + "New-Item -ItemType Directory -Force <フォルダ> で作成してから同じコマンドを再実行してください。";
        return null;
    }

    private static string MakeNonInteractive(string command)
    {
        var trimmedStart = command.TrimStart();
        if (!trimmedStart.StartsWith("git ", System.StringComparison.OrdinalIgnoreCase))
            return command;
        if (trimmedStart.StartsWith("git --no-pager ", System.StringComparison.OrdinalIgnoreCase)
            || trimmedStart.StartsWith("git -P ", System.StringComparison.OrdinalIgnoreCase))
            return command;

        var leading = command[..(command.Length - trimmedStart.Length)];
        return leading + "git --no-pager " + trimmedStart[4..];
    }
}
