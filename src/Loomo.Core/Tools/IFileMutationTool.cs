using System.Text.Json;

namespace sk0ya.Loomo.Core.Tools;

/// <summary>
/// ファイルを変更するツールが実装する任意の追加IF。<see cref="Agent.AgentOrchestrator"/> が
/// 「<b>同一ターン内で直前に変更したファイルを、さらに全文上書きで破壊する</b>」冗長アクションを
/// 決定論的に防ぐために使う。小モデルは正しい編集（例: edit_file で 1 箇所置換）に成功した直後に、
/// 同じファイルへ write_file で短い内容を全文上書きして<b>本文を丸ごと破壊</b>することがある
/// （実測の主要失敗モード）。これはプロンプトでは安定して抑えられないため、ループ側の決定論的ガードで止める。
/// </summary>
public interface IFileMutationTool
{
    /// <summary>この呼び出しが対象とするファイルの canonical な絶対パス（ワークスペース解決済み）。
    /// パス未指定・ルート外・解決失敗なら <c>null</c>。引数は <see cref="IAgentTool.NormalizeArguments"/> 済みを渡す。
    /// 相対指定（README.md）と絶対指定（C:\…\README.md）を<b>同一キーに正規化</b>するのが目的
    /// （小モデルは同じファイルを相対／絶対で混在させるため、文字列比較では同一性を取りこぼす）。</summary>
    string? ResolveTargetPath(JsonElement normalizedArguments);

    /// <summary><c>true</c> なら全文上書き（write_file）。同一ターンで既に変更済みのファイルへの
    /// 全文上書きは破壊的とみなしてブロックする。<c>false</c>（edit_file の一意置換・追記）は対象箇所だけの
    /// 変更なのでブロックしない（多段の絞り込み編集は正当）。</summary>
    bool FullyOverwritesTarget { get; }
}
