using System.Collections.Concurrent;
using System.IO;
using System.Text.Json.Nodes;
using sk0ya.Loomo.Core.Motion;
using sk0ya.Loomo.Services.ClaudeCode;
using sk0ya.Loomo.Services.Motion;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: 「動きが悪くなったら Claude に直させる」（§45.6 P3）。Claude のターンが終わる直前（Stop フック）に、
/// 許された見どころを録り、悪い方へ変わっていれば <c>decision: block</c> を返して Claude に直させる。
/// <para>既定は切（<see cref="sk0ya.Loomo.Core.Settings.LoomoSettings.ClaudeContinueOnBrokenMotion"/>）。1 つの頼みごとで最大
/// <see cref="ClaudeStopDecision.MaxContinuationsPerPrompt"/> 回まで。止めずに済ませた悪化は、次の頼みごとへの知らせに回す。
/// 入れ子の claude（claude -p など）は止めない——人間が向かっている claude ではないので。</para>
/// </summary>
public partial class ShellWindow {
    /// <summary>この頼みごとで続けさせた回数（セッションごと）。次の頼みごと（UserPromptSubmit）で消す。</summary>
    private readonly ConcurrentDictionary<string, int> _continuations = new(StringComparer.Ordinal);

    private async Task<JsonObject?> ClaudeStopDecisionAsync(LoomoCaller caller, JsonObject hookEvent) {
        if (!_settings.ClaudeContinueOnBrokenMotion || caller.Nested)
            return null;
        var session = (string?)hookEvent["session_id"];
        if (string.IsNullOrEmpty(session) || _workspace.PrimaryFolder is not { } folder)
            return null;
        if (!_motion.ApprovedProbes(folder).Any(probe => probe.EveryTurn))
            return null;
        var before = _continuations.GetOrAdd(session, 0);
        if (before >= ClaudeStopDecision.MaxContinuationsPerPrompt)
            return null;

        // 録り終えるまで待つ（Stop フックの待ち時間の内側に収める）。録れなかったら止めてよい。
        MotionSnapshot? snapshot;
        try {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(80));
            snapshot = await _motion.RecordAllAsync(folder, new MotionOrigin(session, 0, null, MotionSnapshot.TriggerStop), limit.Token);
        } catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException) {
            return null;
        }
        if (snapshot is null)
            return null;

        var probes = _motion.Store.LoadProbes(folder, out _);
        var changes = _motion.Store.Compare(folder, snapshot, probes);
        var headline = MotionDiff.Headline(changes);
        if (!MotionDiff.Worsened(changes) || headline is null)
            return null;

        var block = ClaudeStopDecision.Block(headline, before);
        if (block is null) {
            // 上限に達した：止めて、悪化は次の頼みごとへの知らせに回す（黙って捨てない）。
            _claudeNotes.Add(session, new ClaudeNote(ClaudeNote.KindMotionWorsened,
                $"直前のターンのあと、動きが悪い方へ変わりました（直させる回数の上限に達したため止まりました）：{headline}。", DateTimeOffset.Now));
            return null;
        }
        _continuations[session] = before + 1;
        return block;
    }
}
