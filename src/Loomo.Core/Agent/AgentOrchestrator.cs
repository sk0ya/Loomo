using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Diff;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Observability;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Core.Tools;
using Microsoft.Extensions.Logging;

namespace sk0ya.Loomo.Core.Agent;

/// <summary>
/// エージェントループ本体（UI非依存）。
/// user入力 → AI → tool_use → (承認) → tool実行 → 結果をAIへ → … → 最終テキスト。
/// </summary>
public sealed class AgentOrchestrator
{
    private readonly IAiClientFactory _aiFactory;
    private readonly ToolRegistry _tools;
    private readonly IApprovalService _approval;
    private readonly ISafetyPolicy _safety;
    private readonly IContextWindowPolicy _context;
    private readonly ITraceSink _trace;
    private readonly IFileChangeJournal? _journal;
    private readonly ILogger<AgentOrchestrator> _logger;

    private const int MaxIterations = 25;

    /// <summary>不正なツール呼び出しJSONを AI に出し直させる最大回数（1ターン内）。
    /// 修正できないモデルが延々と prefill を浪費するのを防ぐ上限。超えたらエラーで打ち切る。</summary>
    private const int MaxToolCallParseRetries = 2;

    /// <summary>同一（ツール名＋引数）の<b>失敗</b>呼び出しがこの回数に達したらターンを打ち切る上限。
    /// 小モデルは同じ不正引数（例: 空 old_string での edit_file）を延々と再生産し、結果を見ても
    /// 引数を変えないことがある。MaxIterations(25) まで回すと prefill/decode を空費するため、
    /// 「同じ失敗を繰り返している」ことを検知して早期に止める安全網。</summary>
    private const int MaxRepeatedToolFailures = 3;

    public AgentOrchestrator(
        IAiClientFactory aiFactory,
        ToolRegistry tools,
        IApprovalService approval,
        ISafetyPolicy safety,
        IContextWindowPolicy context,
        ILogger<AgentOrchestrator> logger,
        ITraceSink? trace = null,
        IFileChangeJournal? journal = null)
    {
        _aiFactory = aiFactory;
        _tools = tools;
        _approval = approval;
        _safety = safety;
        _context = context;
        _logger = logger;
        _trace = trace ?? NullTraceSink.Instance;
        _journal = journal;
    }

    /// <summary>ユーザー入力を処理してイベントを流す。</summary>
    /// <param name="sessionId">トレース記録用のセッションID（§20）。null/空ならトレースは "unknown" にまとまる。</param>
    /// <param name="toolDefinitionsOverride">このターンでモデルへ提示するツール定義を差し替える。
    /// 既定（null）は全登録ツール（通常のエージェントループ）。空配列を渡すとツール無し＝モデルは
    /// テキストしか返せず、1回の応答で即 <see cref="TurnCompleted"/> する。</param>
    public async IAsyncEnumerable<AgentEvent> RunTurnAsync(
        Conversation conversation,
        string userInput,
        string? sessionId = null,
        [EnumeratorCancellation] CancellationToken ct = default,
        AgentProfile? profile = null,
        IReadOnlyList<ToolDefinition>? toolDefinitionsOverride = null)
    {
        sessionId ??= "unknown";
        var activeProfile = profile ?? AgentProfiles.Root;
        var isNewSession = conversation.Messages.Count == 0;

        conversation.AddUser(userInput);
        var ai = _aiFactory.ResolveCurrent();
        var definitions = toolDefinitionsOverride ?? _tools.Definitions;

        var turnId = Guid.NewGuid().ToString("N");
        var provider = ai.Provider.ToString();
        var turnClock = Stopwatch.StartNew();
        var parseRetries = 0;
        // ターン内で「同一の失敗ツール呼び出し」が何回出たかを数える（暴走ループの早期打ち切り用）。
        var failureCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        // 直前の反復のツール結果にエラーが含まれていたか（虚偽成功ガード用）。
        var lastBatchHadError = false;
        // 虚偽成功ガードの差し戻しを使ったか（1ターン1回だけ。無限の押し問答を防ぐ）。
        var errorStopNudged = false;
        // ターン内で edit_file の対象限定編集（部分置換・追記）に成功したファイルの canonical 絶対パス。
        // この後に同じファイルへ write_file の全文上書きが来たら、直前の編集を丸ごと破壊するため決定論的に
        // ブロックする（小モデルが正しい編集の直後に全文上書きで本文を壊す主要失敗モードの対策）。
        // write_file→write_file（自分で全文を書いた直後の全文書き直し）は破壊ではないので記録しない＝許可する。
        // 反復をまたいで保持する。
        var editedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (isNewSession)
            _trace.Record(sessionId, null, TraceKinds.SessionStarted, new { provider, agentId = activeProfile.Id });
        _trace.Record(sessionId, turnId, TraceKinds.TurnStarted, new
        {
            userInput,
            provider,
            agentId = activeProfile.Id,
        });

        for (var iteration = 0; iteration < MaxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            var assistant = new ChatMessage { Role = ChatRole.Assistant };
            var pendingToolUses = new List<ToolUse>();
            var toolResults = new List<ToolResultMessage>();   // インライン実行した結果（配列順）
            var sawModelOutput = false;
            ToolCallParseFailed? parseFailed = null;
            // 単段ループ：全反復で同じ profile（既定は Root）と同じツール集合を使う。
            // system プロンプトとツール配列を反復間でバイト不変に保つことで、Ollama の
            // プレフィックスKVキャッシュが効き、prefill を毎ターン払い直さずに済む。
            var activeDefinitions = definitions;

            // コンテキスト超過を防ぐため、送信用に履歴をトリム（元会話は保持）。
            var outgoing = _context.Fit(conversation, activeProfile);

            // --- AIストリームを消費 ---
            AgentError? streamError = null;
            _trace.Record(sessionId, turnId, TraceKinds.AiMessage, new
            {
                agentId = activeProfile.Id,
                stage = activeProfile.DisplayName,
                started = true,
                iteration = iteration + 1,
                toolCount = activeDefinitions.Count,
            });
            await foreach (var ev in ai.StreamAsync(outgoing, activeDefinitions, ct, activeProfile,
                               retryDiversify: parseRetries > 0))
            {
                switch (ev)
                {
                    case RawTextDelta rawDelta:
                        // 揮発性のライブ出力：確定本文ではないので履歴へ積まず（sawModelOutput も立てない）、
                        // UI の進捗プレビュー用にそのまま素通しする。確定は終端の TextDelta／ToolUseRequested が担う。
                        yield return rawDelta;
                        break;
                    case TextDelta delta:
                        sawModelOutput = true;
                        assistant.Text = (assistant.Text ?? string.Empty) + delta.Text;
                        yield return delta;
                        break;
                    case ThinkingDelta thinking:
                        sawModelOutput = true;
                        // 思考は応答本文ではないため履歴へは積まず、UI表示用に通すだけ。
                        yield return thinking;
                        break;
                    case ToolUseRequested req:
                        sawModelOutput = true;
                        assistant.ToolUses.Add(req.ToolUse);
                        pendingToolUses.Add(req.ToolUse);
                        yield return req;

                        // 検知した時点で即実行する（全文確定を待たない）。await 中もエンジンは次のオブジェクトを
                        // 背景でデコードし続けるため、生成とツール実行が重なる。実行は配列どおりの順序を維持する。
                        _trace.Record(sessionId, turnId, TraceKinds.AiToolUse, new
                        {
                            toolUseId = req.ToolUse.Id,
                            name = req.ToolUse.Name,
                            argsJson = req.ToolUse.ArgumentsJson,
                            rawJson = req.ToolUse.RawJson,
                            agentId = activeProfile.Id,
                            stage = activeProfile.DisplayName,
                        });
                        // 実行中のイベント（承認待ち・実行中…）を貯め込まず即時に流す。Channel で実行タスクと
                        // 並行に読み出すことで、承認待ちや長い実行の状態がリアルタイムにUIへ届く。
                        var execChannel = Channel.CreateUnbounded<AgentEvent>();
                        var execTask = ExecuteToolWithEventsAsync(req.ToolUse, sessionId, turnId, execChannel.Writer, editedPaths, ct);
                        // ct は渡さない：writer は finally で必ず Complete されるので読み出しは自然に終わり、
                        // キャンセルは execTask の await で観測される（未観測例外を防ぐ）。
                        await foreach (var e in execChannel.Reader.ReadAllAsync())
                            yield return e;
                        toolResults.Add(await execTask);
                        break;
                    case ToolCallParseFailed pf:
                        // ツール呼び出しらしき不正JSON。生出力をアシスタント本文として残し（モデルが自分の
                        // 誤りを次ターンで見られる）、UI へも流して可視化する。再試行はストリーム終了後に判断。
                        sawModelOutput = true;
                        assistant.Text = (assistant.Text ?? string.Empty) + pf.RawText;
                        parseFailed = pf;
                        yield return pf;
                        break;
                    case AiUsageReported usage:
                        // 利用統計はモデル出力ではないので sawModelOutput は立てない。
                        // トークン数と段階別の所要（重みロード/prefill/decode）を ai.usage として記録し、
                        // UI（進行状況）にも流して、どの段階が遅いかをその場で見えるようにする。
                        _trace.Record(sessionId, turnId, TraceKinds.AiUsage, new
                        {
                            inputTokens = usage.InputTokens,
                            outputTokens = usage.OutputTokens,
                            loadMs = usage.LoadMs,
                            promptEvalMs = usage.PromptEvalMs,
                            evalMs = usage.EvalMs,
                            totalMs = usage.TotalMs,
                            iteration = iteration + 1,
                            agentId = activeProfile.Id,
                        });
                        yield return usage;
                        break;
                    case AgentError err:
                        streamError = err;
                        break;
                }
                if (streamError is not null) break;
            }

            if (streamError is not null)
            {
                _trace.Record(sessionId, turnId, TraceKinds.Error,
                    new { message = streamError.Message, where = "ai.stream" });
                yield return streamError;
                yield break;
            }

            if (!sawModelOutput)
            {
                var emptyError = new AgentError("AI から応答が返りませんでした。ローカルLLMの起動状態とモデル名を確認してください。");
                _trace.Record(sessionId, turnId, TraceKinds.Error,
                    new { message = emptyError.Message, where = "ai.empty_response" });
                yield return emptyError;
                yield break;
            }

            // ストリーム終了時にアシスタント本文をまとめて記録（TextDelta を合算した全文）。
            if (!string.IsNullOrEmpty(assistant.Text))
                _trace.Record(sessionId, turnId, TraceKinds.AiMessage, new
                {
                    fullText = assistant.Text,
                    agentId = activeProfile.Id,
                    stage = activeProfile.DisplayName,
                });
            // 本文もツール呼び出しも無い空応答は履歴に積まない（APIエラー要因になり得る）。
            if (!string.IsNullOrEmpty(assistant.Text) || pendingToolUses.Count > 0)
                conversation.Messages.Add(assistant);

            if (pendingToolUses.Count == 0)
            {
                // 不正なツール呼び出しJSON：終端にせず、誤りを差し戻して AI に正しいJSONで出し直させる。
                if (parseFailed is not null)
                {
                    if (parseRetries < MaxToolCallParseRetries)
                    {
                        parseRetries++;
                        _trace.Record(sessionId, turnId, TraceKinds.Error, new
                        {
                            message = "ツール呼び出しJSONを解釈できませんでした。再試行します。",
                            where = "toolcall.parse",
                            attempt = parseRetries,
                            raw = parseFailed.RawText,
                        });
                        conversation.AddUser(
                            "前の返信はツール呼び出しのJSONとして解釈できませんでした。ツールを使うなら、" +
                            "配列にオブジェクトを1つだけ含む正しいJSONで出し直してください" +
                            "（キーと値は \":\" 区切り、文字列内の \" は \\\" でエスケープ）。" +
                            "ツールが不要なら日本語の文章だけで答えてください。");
                        continue;   // 次の反復で再生成
                    }

                    // 上限到達：生出力を添えてエラーで打ち切る（何が出たかは残す）。
                    var giveUp = $"ツール呼び出しのJSONを{MaxToolCallParseRetries + 1}回連続で解釈できませんでした。"
                                 + "モデルの出力が不正なJSONのままのため中断します。\n--- モデルの出力 ---\n"
                                 + parseFailed.RawText;
                    _trace.Record(sessionId, turnId, TraceKinds.Error,
                        new { message = giveUp, where = "toolcall.parse.giveup" });
                    yield return new AgentError(giveUp);
                    yield break;
                }

                // 虚偽成功ガード：直前の反復でツールが失敗したのに、ツールを呼ばず最終回答で終わろうと
                // した場合、1ターンに1回だけ差し戻す。小モデルはエラーの後に「完了しました」と虚偽報告
                // したり、エラーを「見つかりませんでした」等の偽の事実に変換したりする（実測の主要残存
                // 故障）。修正再実行か正直な失敗報告かをモデルに選ばせる（正直な報告なら次の反復で
                // そのまま終端する。ガードは挙動を強制しない＝誤発火しても1反復ぶんのコストで済む）。
                if (lastBatchHadError && !errorStopNudged)
                {
                    errorStopNudged = true;
                    _trace.Record(sessionId, turnId, TraceKinds.Error, new
                    {
                        message = "ツール失敗直後の最終回答を差し戻し（虚偽成功ガード）。",
                        where = "turn.error_stop_nudge",
                        finalText = assistant.Text,
                    });
                    // 「修正して再実行か正直な報告か」だけだと、小モデルは修正版コマンドを文章で提案して
                    // 実行せずに終わる（実測）。実行を第一指示にし、説明だけの回答を明示的に禁じる。
                    conversation.AddUser(
                        "直前のツール呼び出しは失敗しています。エラーメッセージに基づいて呼び出しを修正し、" +
                        "今すぐツールを再実行してください。修正案や原因をツールを呼ばずに文章で説明しては" +
                        "いけません。どうしても回復できない場合のみ、何が失敗したかを正直に報告してください。" +
                        "失敗した操作を完了済みとして報告してはいけません。");
                    continue;
                }

                // ツール呼び出しが無ければターン終了
                _trace.Record(sessionId, turnId, TraceKinds.TurnCompleted,
                    new { finalText = assistant.Text, iterations = iteration + 1, durationMs = turnClock.ElapsedMilliseconds });
                yield return new TurnCompleted(assistant.Text);
                yield break;
            }

            // ツールはストリーム検知時にインライン実行済み（生成と重ねて配列順に実行）。結果を1つの
            // tool メッセージにまとめ、アシスタントメッセージ直後に積む（tool_result はアシスタント直後に
            // 来る不変条件を維持）。
            var toolMessage = new ChatMessage { Role = ChatRole.Tool };
            toolMessage.ToolResults.AddRange(toolResults);
            conversation.Messages.Add(toolMessage);

            // 虚偽成功ガード用：この反復の結果にエラーが含まれていたかを記録する。
            lastBatchHadError = false;
            foreach (var r in toolResults)
                if (r.IsError) { lastBatchHadError = true; break; }

            // 進捗ガード：同一（ツール名＋引数）の失敗呼び出しがしきい値に達したら、これ以上反復しても
            // 同じゴミを再生産するだけなので打ち切る。pendingToolUses と toolResults は配列順で対応する。
            for (var i = 0; i < toolResults.Count && i < pendingToolUses.Count; i++)
            {
                if (!toolResults[i].IsError) continue;
                var use = pendingToolUses[i];
                var sig = use.Name + "" + (use.ArgumentsJson ?? string.Empty);
                var count = failureCounts[sig] = failureCounts.GetValueOrDefault(sig) + 1;
                if (count < MaxRepeatedToolFailures) continue;

                var stuck = $"同じツール呼び出し（{use.Name}）が{count}回連続で同じ失敗を返しました。"
                            + "引数が改善していないため、これ以上の反復を中断します。"
                            + "別のアプローチが必要です。\n--- 最後のエラー ---\n" + toolResults[i].Content;
                _trace.Record(sessionId, turnId, TraceKinds.Error, new
                {
                    message = stuck,
                    where = "toolcall.repeat_failure",
                    tool = use.Name,
                    count,
                });
                yield return new AgentError(stuck);
                yield break;
            }
            // ループ継続 → 結果を踏まえてAIが再応答
        }

        _trace.Record(sessionId, turnId, TraceKinds.Error,
            new { message = $"最大反復回数({MaxIterations})に達しました。", where = "iteration.limit" });
        yield return new AgentError($"最大反復回数({MaxIterations})に達しました。");
    }

    /// <summary><see cref="ExecuteToolAsync"/> を実行し、終了時に必ずイベントチャネルを閉じる。
    /// これにより呼び出し側の読み出しループが確実に終了する。</summary>
    private async Task<ToolResultMessage> ExecuteToolWithEventsAsync(
        ToolUse use,
        string sessionId,
        string turnId,
        ChannelWriter<AgentEvent> events,
        HashSet<string> editedPaths,
        CancellationToken ct)
    {
        try { return await ExecuteToolAsync(use, sessionId, turnId, events, editedPaths, ct); }
        finally { events.Complete(); }
    }

    private async Task<ToolResultMessage> ExecuteToolAsync(
        ToolUse use,
        string sessionId,
        string turnId,
        ChannelWriter<AgentEvent> events,
        HashSet<string> editedPaths,
        CancellationToken ct)
    {
        if (!_tools.TryGet(use.Name, out var tool))
        {
            var msg = $"未知のツール: {use.Name}";
            _logger.LogWarning("{Message}", msg);
            _trace.Record(sessionId, turnId, TraceKinds.Error, new { message = msg, where = "tool.resolve" });
            return new ToolResultMessage(use.Id, msg, IsError: true);
        }

        JsonElement args;
        try
        {
            using var doc = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(use.ArgumentsJson) ? "{}" : use.ArgumentsJson);
            args = doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _trace.Record(sessionId, turnId, TraceKinds.Error, new { message = ex.Message, where = "tool.args" });
            return new ToolResultMessage(use.Id, $"引数JSONの解析失敗: {ex.Message}", IsError: true);
        }

        // キー揺れ等を canonical へ寄せる。安全評価・要約・実行が同じ正規化済み引数を見るよう、ここで一度だけ適用する。
        try { args = tool.NormalizeArguments(args); }
        catch (Exception ex)
        {
            _trace.Record(sessionId, turnId, TraceKinds.Error, new { message = ex.Message, where = "tool.normalize" });
            // 正規化に失敗しても元の引数のまま続行（安全評価・実行は通常どおり行う）。
        }

        // 冗長な破壊的上書きガード：同一ターンで edit_file が対象限定編集したファイルを、後続の write_file が
        // 全文上書きしようとしたら、実行せず差し戻す（直前の正しい編集を丸ごと破壊しない）。edit_file 自身の
        // 対象限定変更・追記は対象外（多段の絞り込み編集は正当）。write_file→write_file（自分で書いた全文の
        // 書き直し）も破壊ではないので対象外。pwsh など IFileMutationTool 非実装のツールも対象外。
        if (tool is IFileMutationTool mutation && mutation.FullyOverwritesTarget)
        {
            var target = SafeResolveTarget(mutation, args);
            if (target is not null && editedPaths.Contains(target))
            {
                var msg = "このターンで edit_file が編集したファイルを write_file で全文上書きしようとしました（"
                          + target + "）。直前の編集が破棄されるためブロックしました。変更は完了しています。"
                          + "やり直しは不要なので、ツールを呼ばず日本語で結果を報告してください。"
                          + "さらに修正が必要な場合のみ、edit_file で対象箇所だけを変更してください。";
                _logger.LogWarning("冗長な破壊的上書きをブロック: {Path}", target);
                _trace.Record(sessionId, turnId, TraceKinds.Error,
                    new { message = msg, where = "tool.redundant_overwrite", path = target });
                var blocked = new ToolResultMessage(use.Id, msg, IsError: true);
                events.TryWrite(new ToolExecutionStarted(use));
                events.TryWrite(new ToolExecutionCompleted(use, blocked));
                return blocked;
            }
        }

        // 安全ポリシー：危険コマンドのブロックリストに一致したら実行せず差し戻す
        var decision = _safety.Evaluate(tool.Name, args);
        _trace.Record(sessionId, turnId, TraceKinds.SafetyEvaluated,
            new { tool = tool.Name, blocked = decision.Blocked, reason = decision.Reason });
        if (decision.Blocked)
        {
            _logger.LogWarning("安全ポリシーによりブロック: {Tool} — {Reason}", tool.Name, decision.Reason);
            var blocked = new ToolResultMessage(use.Id, decision.Reason!, IsError: true);
            events.TryWrite(new ToolExecutionStarted(use));
            events.TryWrite(new ToolExecutionCompleted(use, blocked));
            return blocked;
        }

        // 承認（自動承認モードが有効ならスキップ）
        if (tool.RequiresApproval && !_safety.AutoApprove)
        {
            var summary = SafeDescribe(tool, args);
            events.TryWrite(new ApprovalRequested(tool.Name, summary));
            _trace.Record(sessionId, turnId, TraceKinds.ApprovalRequested, new { tool = tool.Name, summary });
            var approvalClock = Stopwatch.StartNew();
            var approved = await _approval.RequestApprovalAsync(tool.Name, summary, ct);
            _trace.Record(sessionId, turnId, TraceKinds.ApprovalResolved,
                new { tool = tool.Name, approved, waitMs = approvalClock.ElapsedMilliseconds });
            if (!approved)
                return new ToolResultMessage(use.Id, "ユーザーが実行を拒否しました。", IsError: true);
        }

        // Diff セッション用：ファイル変更ツールは実行前の全文を控えておき、成功後に前後ペアで記録する。
        string? journalPath = null;
        var journalExistedBefore = false;
        string? journalBefore = null;
        if (_journal is not null && tool is IFileMutationTool journalTool)
        {
            journalPath = SafeResolveTarget(journalTool, args);
            if (journalPath is not null)
                (journalExistedBefore, journalBefore) = FileChangeJournal.SafeReadFile(journalPath);
        }

        events.TryWrite(new ToolExecutionStarted(use));
        _trace.Record(sessionId, turnId, TraceKinds.ToolStarted, new { toolUseId = use.Id, name = tool.Name });
        var toolClock = Stopwatch.StartNew();
        try
        {
            var result = await tool.ExecuteAsync(args, ct);
            var content = NormalizeToolResultContent(tool.Name, result);
            var resultMessage = new ToolResultMessage(use.Id, content, result.IsError);
            // 対象限定編集（edit_file）に成功したファイルだけ記録：以降の反復で同じファイルへの破壊的な
            // 全文上書き（write_file）をガードする。write_file 自身の成功は記録しない（全文の書き直しは破壊でない）。
            if (!result.IsError && tool is IFileMutationTool fileTool && !fileTool.FullyOverwritesTarget)
            {
                var target = SafeResolveTarget(fileTool, args);
                if (target is not null) editedPaths.Add(target);
            }
            // ファイル変更に成功したら実行後の全文を読み、前後ペアをジャーナルへ記録する（Diff セッション用）。
            if (!result.IsError && journalPath is not null && _journal is not null)
            {
                var (_, journalAfter) = FileChangeJournal.SafeReadFile(journalPath);
                if (!journalExistedBefore || !string.Equals(journalBefore, journalAfter, StringComparison.Ordinal))
                    _journal.Record(new FileChangeRecord(
                        DateTimeOffset.Now, sessionId, turnId, tool.Name, journalPath,
                        IsNew: !journalExistedBefore, journalBefore, journalAfter));
            }
            _trace.Record(sessionId, turnId, TraceKinds.ToolCompleted, new
            {
                toolUseId = use.Id,
                name = tool.Name,
                isError = result.IsError,
                durationMs = toolClock.ElapsedMilliseconds,
                contentLen = content.Length,
                originalContentLen = result.Content?.Length ?? 0,
            });
            events.TryWrite(new ToolExecutionCompleted(use, resultMessage));
            return resultMessage;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ツール実行エラー: {Tool}", tool.Name);
            _trace.Record(sessionId, turnId, TraceKinds.ToolCompleted, new
            {
                toolUseId = use.Id,
                name = tool.Name,
                isError = true,
                durationMs = toolClock.ElapsedMilliseconds,
                error = ex.Message,
            });
            var resultMessage = new ToolResultMessage(use.Id, $"ツール例外: {ex.Message}", IsError: true);
            events.TryWrite(new ToolExecutionCompleted(use, resultMessage));
            return resultMessage;
        }
    }

    private static string SafeDescribe(IAgentTool tool, JsonElement args)
    {
        try { return tool.DescribeInvocation(args); }
        catch { return tool.Name; }
    }

    /// <summary>ファイル変更ツールの対象パスを安全に解決（例外は null 扱い）。冗長上書きガードの比較キー用。</summary>
    private static string? SafeResolveTarget(IFileMutationTool tool, JsonElement args)
    {
        try { return tool.ResolveTargetPath(args); }
        catch { return null; }
    }

    private static string NormalizeToolResultContent(string toolName, ToolResult result)
    {
        if (!string.IsNullOrEmpty(result.Content))
            return result.Content;

        var state = result.IsError ? "failed" : "completed";
        return $"tool {state}: {toolName} (no output)";
    }
}
