using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Core.Tools;

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

    /// <summary>プロバイダ差や小モデルのキー揺れ（command を cmd/script 等で送る等）を canonical な引数へ正規化する。
    /// 既定は恒等（変換なし）。オーケストレータが<b>安全評価の前に一度だけ</b>適用するため、ここで別名を寄せても
    /// 安全評価・要約・実行が同じ正規化済み引数を見る（評価をすり抜けない）。</summary>
    JsonElement NormalizeArguments(JsonElement arguments) => arguments;

    /// <summary>実行。</summary>
    Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct);
}
