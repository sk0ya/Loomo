using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// ONNX Runtime GenAI（<see cref="ILocalInferenceEngine"/> 実体は <see cref="Phi4Engine"/>）を使う
/// ローカルLLMクライアント。Ollama HTTP クライアントの置き換え。
/// プロンプトを <see cref="Phi4PromptFormatter"/> で組み立ててエンジンへ渡し、本文をバッファして
/// 終端でツール呼び出しを判定する（小型モデルは構造化 tool_calls を出さず本文に JSON 配列で書くため、
/// <see cref="ToolCallTextParser"/> で復元する）。本文判定は旧 Ollama クライアントの終端処理と同型。
/// </summary>
public sealed class OnnxGenAiClient : IAiClient
{
    private readonly ILocalInferenceEngine _engine;
    private readonly AiSettings _settings;
    private readonly IWorkspaceService _workspace;

    public OnnxGenAiClient(ILocalInferenceEngine engine, AiSettings settings, IWorkspaceService workspace)
    {
        _engine = engine;
        _settings = settings;
        _workspace = workspace;
    }

    public AiProvider Provider => AiProvider.Local;

    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        Conversation conversation,
        IReadOnlyList<ToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken ct,
        AgentProfile? profile = null,
        bool retryDiversify = false)
    {
        var cfg = _settings.Local;
        var modelProfile = ModelProfiles.Resolve(cfg.Model);
        var prompt = ChatPrompt.Build(modelProfile.Format, _settings, profile, _workspace.RootPath, conversation, tools);
        var maxLength = ModelProfiles.EffectiveNumCtx(cfg.Model, cfg.NumCtx);
        var maxNewTokens = modelProfile.MaxOutputTokens > 0
            ? Math.Min(cfg.MaxTokens, modelProfile.MaxOutputTokens)
            : cfg.MaxTokens;
        // ツール呼び出しJSON解釈失敗からの再試行時は、同じ不正出力を再生産して prefill を無駄に払い直さない
        // よう出力を散らす。⚠ ここで temperature/top_p だけ上げても無意味だった：genai_config の既定が
        // top_k=1（phi4・qwen3 とも実測）で、候補が1つに絞られ温度が効かず greedy のまま＝3回ともバイト同一の
        // ゴミを再生産していた。top_k を明示的に開く（候補プールを作る）ことで初めて温度/top_p が効き、リトライが
        // 実際に分岐する。通常時はモデル別プロファイルのサンプリング（qwen3 は top_k=20、phi4 は未指定＝greedy）。
        var sampling = retryDiversify
            ? new SamplingOptions(Temperature: 0.8, TopP: 0.95, TopK: 40)
            : modelProfile.Sampling;
        var request = new GenerationRequest(prompt, cfg.ModelPath ?? "", maxLength, maxNewTokens, sampling);

        var channel = Channel.CreateUnbounded<AgentEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var genTask = _engine.GenerateAsync(request, channel.Writer, ct);

        var finalText = new StringBuilder();
        var sawText = false;
        AgentError? error = null;
        // ストリーム中にツール呼び出し配列のオブジェクトが閉じるたび、終端を待たず ToolUseRequested を前倒しで流す。
        // これによりオーケストレータは「実行できるツールから」順次（生成と重ねて）実行できる。
        var scanner = new StreamingToolCallScanner();

        await foreach (var ev in channel.Reader.ReadAllAsync(ct))
        {
            switch (ev)
            {
                case TextDelta td:
                    // 本文はバッファし、終端でツール呼び出しか通常テキストかを判定してから確定出力する。
                    // ただし生成中の様子は見えるよう、生のチャンクを揮発性の RawTextDelta として先に流す
                    // （履歴には積まれず、UI の進捗プレビュー専用）。
                    sawText = true;
                    finalText.Append(td.Text);
                    yield return new RawTextDelta(td.Text);
                    // 加えて、完成したツール呼び出しがあれば即座に確定通知（早期ディスパッチ）。
                    foreach (var tc in scanner.Feed(td.Text))
                        yield return new ToolUseRequested(tc);
                    break;
                case AiUsageReported usage:
                    yield return usage;     // 利用統計はそのまま通知（記録目的）
                    break;
                case AgentError e:
                    error = e;
                    break;
                default:
                    yield return ev;        // 念のため他イベントは素通し
                    break;
            }
        }

        await genTask;      // 例外・後始末の観測

        if (error is not null)
        {
            yield return error;
            yield break;
        }

        var text = finalText.ToString();

        // ストリーム中に配列モードでツール呼び出しを出し切っていれば、ここで確定（TurnCompleted は出さない＝ツール継続）。
        if (scanner.EmittedCount > 0)
        {
            // 配列の外側にモデルが書いた自然文（前置き＝通常は空白のみ／配列後の後置き）は、ここまで揮発性の
            // RawTextDelta でしか流れていない。確定本文として TextDelta で出し、履歴に残す（捨てない）。
            var surrounding = ExtractTextOutsideArray(text, scanner);
            if (!string.IsNullOrEmpty(surrounding))
                yield return new TextDelta(surrounding);
            yield break;
        }

        // 何も前倒しできなかった場合のみ、実績ある終端パーサで判定する
        // （単体 {…}／run_powershell(...)／コードフェンス／先頭欠落の復元／通常テキスト／不正JSON）。
        var toolCalls = ToolCallTextParser.Parse(text);

        foreach (var tc in toolCalls)
            yield return new ToolUseRequested(tc);

        if (!sawText && toolCalls.Count == 0)
        {
            yield return new AgentError(
                "ローカルモデルから応答本文が返りませんでした。モデルフォルダの指定とモデルの整合性を確認してください。");
            yield break;
        }

        if (toolCalls.Count == 0)
        {
            // 最終回答として出す前に thinking ブロックを除去する（Qwen3 を no_think で動かしても
            // 空の <think></think> が残ることがあり、本文へ混ぜない）。phi4 では no-op。
            var clean = ToolCallTextParser.StripThinkBlocks(text);

            // ツール呼び出しらしき本文なのに 1 件も解釈できなかった＝不正な JSON。生の JSON を最終回答として
            // 黙って出す（実行されず "何も起きない" ように見える）のではなく、生出力ごと差し戻して
            // オーケストレータに再試行させる（AI が自己修正できる／UI が何を出したか見られる）。
            if (LooksLikeToolCallAttempt(clean))
            {
                yield return new ToolCallParseFailed(clean);
                yield break;
            }
            if (!string.IsNullOrEmpty(clean))
                yield return new TextDelta(clean);
            yield return new TurnCompleted(clean);
        }
    }

    /// <summary>ツール呼び出し配列の外側にモデルが書いた自然文（前置き＝通常は空白のみ／配列後の後置き）を取り出す。
    /// 配列を綺麗に閉じている（<see cref="StreamingToolCallScanner.JsonEndIndex"/> が 0 以上）ときだけ境界が確定するので、
    /// それ以外は空を返す（未完／不正要素で打ち切った残骸を本文へ混ぜない）。</summary>
    private static string ExtractTextOutsideArray(string fullText, StreamingToolCallScanner scanner)
    {
        if (scanner.JsonEndIndex < 0) return string.Empty;
        var leading = scanner.JsonStartIndex > 0 ? fullText[..scanner.JsonStartIndex] : string.Empty;
        var trailing = scanner.JsonEndIndex < fullText.Length ? fullText[scanner.JsonEndIndex..] : string.Empty;
        return (leading + trailing).Trim();
    }

    /// <summary>本文がツール呼び出しの試みに見えるか（JSON 配列／オブジェクトで始まり tool call のキーを含む、
    /// あるいは Qwen3 の &lt;tool_call&gt; タグを含む）。パース不能でも生出力を回答として出さず、
    /// 誤りを返して再試行させるための判定。</summary>
    private static bool LooksLikeToolCallAttempt(string text)
    {
        if (text.Contains("<tool_call>", StringComparison.Ordinal)) return true;
        var t = text.TrimStart();
        if (t.Length == 0 || (t[0] != '[' && t[0] != '{')) return false;
        return text.Contains("\"name\"") || text.Contains("\"arguments\"") || text.Contains("\"command\"");
    }
}
