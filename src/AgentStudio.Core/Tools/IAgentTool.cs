using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AgentStudio.Core.Tools;

/// <summary>
/// AIエージェントの「手足」。Terminal/Editor/FileSystem 等のサービスを薄くラップする。
/// 新しい能力の追加は本IFを実装してDI登録するだけ（拡張ポイント）。
/// </summary>
public interface IAgentTool
{
    /// <summary>AIから呼び出される際の一意名（snake_case 推奨）。</summary>
    string Name { get; }

    /// <summary>AIへ渡す定義。</summary>
    ToolDefinition Definition { get; }

    /// <summary>実行前にユーザー承認を要するか（コマンド実行・書込など）。</summary>
    bool RequiresApproval { get; }

    /// <summary>承認カードに表示する短い要約を返す。</summary>
    string DescribeInvocation(JsonElement arguments);

    /// <summary>実行。</summary>
    Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct);
}
