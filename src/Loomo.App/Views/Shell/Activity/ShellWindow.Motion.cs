using System.Text.Json.Nodes;
using sk0ya.Loomo.Core.Viewers;
using sk0ya.Loomo.Core.Harness;
using sk0ya.Loomo.Core.Motion;
using sk0ya.Loomo.Core.Orders;
using sk0ya.Loomo.Services.ClaudeCode;
using sk0ya.Loomo.Services.Viewers;
using sk0ya.Loomo.Services.Motion;
using sk0ya.Loomo.Services.Orders;

namespace sk0ya.Loomo.App.Views;
/// <summary>
/// ShellWindow: 動きで読む（§44.3）と言いつけ（§44.4）の、部屋の側の受け口。
/// <list type="bullet">
/// <item>Claude のターンが終わったら、見どころ（許された <c>when: "turn"</c> のもの）を裏で回して動きを録る。変わっていたら通知。</item>
/// <item>動きが悪い方へ変わったら（テストが落ちるようになった等）、きっかけが「動き」の言いつけを裏のワークツリーで進める。</item>
/// <item>毎日の時刻の言いつけを、時刻が来たら進める（Loomo が動いている間だけ・1 日の回数と使用量の線の内側で）。</item>
/// </list>
/// 見せるビューアは本体に作らず、<see cref="BuiltInViewers"/> が置くビューア（「動き」「言いつけ」）として生やす——口は
/// <see cref="MotionViewerApi"/>・<see cref="OrdersViewerApi"/>。
/// </summary>
public partial class ShellWindow {
    private readonly MotionService _motion = new(new MotionStore());
    private readonly OrderService _orders = new();
    private DispatcherTimer? _orderTimer;

    /// <summary>時刻・動きのきっかけで走らせない、5 時間の使用量の線（%）。</summary>
    private const double OrderUsageLimitPercent = 80;

    private void InitializeMotion() {
        _motion.Recorded += (folder, snapshot) => _ = Task.Run(() => OnMotionRecorded(folder, snapshot));
        _orders.Changed += () => Dispatcher.BeginInvoke(DispatcherPriority.Background,
            () => RealizedViewerPane()?.Emit("ordersChanged", null, ViewerApi.Orders));
        if (_workspace.PrimaryFolder is { } folder)
            _ = Task.Run(() => _orders.RecoverInterrupted(folder));
        _orderTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMinutes(1) };
        _orderTimer.Tick += (_, _) => RunDueOrders();
        _orderTimer.Start();
    }

    // ── 動き ─────────────────────────────────────────────────

    /// <summary>ターンが終わった（<see cref="ApplyClaudeTransition"/> から）。頼みごとを引くのも録るのも裏で。</summary>
    private void RecordMotionAfterTurn(Guid tabId) {
        if (_workspace.PrimaryFolder is not { } folder)
            return;
        var sessionId = _paneActivity.AgentSessionOf(tabId)?.SessionId;
        _ = Task.Run(() => {
            if (!_motion.ApprovedProbes(folder).Any(probe => probe.EveryTurn))
                return;
            ClaudeWrite? latest = null;
            try { latest = ClaudeProvenance.LatestRequest(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            var origin = latest is not null && (sessionId is null || latest.SessionId == sessionId)
                ? new MotionOrigin(latest.SessionId, latest.PromptLine, latest.PromptHead, MotionSnapshot.TriggerTurn)
                : new MotionOrigin(sessionId, 0, null, MotionSnapshot.TriggerTurn);
            _motion.RequestTurnRecord(folder, origin);
        });
    }

    /// <summary>録れた（背景）。比べて、変わっていたら知らせ、悪い方へ変わっていたら言いつけを起こす。
    /// 悪い方へ変わったターンのことは、そのセッションの次の頼みごとに添える（§45.6 P3）。</summary>
    private void OnMotionRecorded(string folder, MotionSnapshot snapshot) {
        var probes = _motion.Store.LoadProbes(folder, out _);
        var changes = _motion.Store.Compare(folder, snapshot, probes);
        var headline = MotionDiff.Headline(changes);
        var worsened = MotionDiff.Worsened(changes);
        if (worsened && headline is not null && snapshot.Trigger == MotionSnapshot.TriggerTurn && snapshot.SessionId is { } session)
            _claudeNotes.Add(session, new ClaudeNote(ClaudeNote.KindMotionWorsened,
                $"直前のターンのあと、動きが悪い方へ変わりました：{headline}。直すか、理由を確かめてください。", DateTimeOffset.Now));
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => {
            RealizedViewerPane()?.Emit("motionRecorded", new JsonObject { ["id"] = snapshot.Id }, ViewerApi.Motion);
            if (snapshot.OrderResultId is not null || headline is null)
                return;
            if (snapshot.Trigger == MotionSnapshot.TriggerTurn)
                ToastService.Info($"動きが変わりました：{headline}（パレット「動きを開く」）");
            if (worsened)
                RunOrdersOnTrigger(StandingOrder.Motion, MotionNote(headline, changes));
        });
    }

    private static string MotionNote(string headline, IReadOnlyList<MotionChange> changes) {
        var failing = changes.SelectMany(change => change.Flips).Where(flip => flip.After == MotionTest.Failed)
            .Select(flip => flip.Name).Take(20).ToArray();
        return "（Loomo より）直前に動きが悪い方へ変わりました：" + headline
               + (failing.Length > 0 ? "\n落ちるようになったテスト：\n- " + string.Join("\n- ", failing) : "");
    }

    private void RecordMotionNow() {
        if (_workspace.PrimaryFolder is not { } folder)
            return;
        if (_motion.ApprovedProbes(folder).Count == 0) {
            ToastService.Info("許された見どころがありません（パレット「動き：見どころを編集」「動き：見どころを許す」）");
            return;
        }
        ToastService.Info("見どころを回しています…");
        _ = Task.Run(async () => {
            var snapshot = await _motion.RecordAllAsync(folder, new MotionOrigin(null, 0, null, MotionSnapshot.TriggerManual), CancellationToken.None);
            if (snapshot is null)
                Dispatcher.Invoke(() => ToastService.Info("録れませんでした（回している間に作業ツリーが変わりました）"));
        });
    }

    private void EditMotionProbes() {
        if (_workspace.PrimaryFolder is not { } folder)
            return;
        try {
            _ = OpenPathInEditorAsync(_motion.Store.EnsureProbesFile(folder), 0, 0);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            ToastService.Error($"見どころの定義を開けませんでした: {ex.Message}");
        }
    }

    /// <summary>見どころの行を人間が許す。行をそのまま見せて聞く（ビューアの許しと同じく、行が変わったら聞き直す）。</summary>
    private void ApproveMotionProbes() {
        if (_workspace.PrimaryFolder is not { } folder)
            return;
        var probes = _motion.Store.LoadProbes(folder, out var error);
        if (error is not null) {
            ToastService.Error($"見どころの定義を読めません: {error}");
            return;
        }
        if (probes.Count == 0) {
            ToastService.Info("見どころがまだありません（パレット「動き：見どころを編集」）");
            return;
        }
        var lines = string.Join("\n\n", probes.Select(probe =>
            $"■ {probe.Title}（{(probe.EveryTurn ? "ターンが終わるたび" : "手で")}・{probe.TimeoutSeconds} 秒まで）\n{probe.Command}"));
        var answer = LoomoMessageBox.Show(this,
            $"次のコマンドを、このワークスペースで裏で回して動きを録ります（見えるターミナルには打ちません）。\n\n{lines}\n\n許しますか？",
            "動き：見どころを許す", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;
        _motion.Store.Approve(folder, probes);
        RealizedViewerPane()?.Emit("motionRecorded", null, ViewerApi.Motion);
        ToastService.Info("見どころを許しました。次のターンの終わりから録ります");
    }

    // ── 言いつけ ─────────────────────────────────────────────

    /// <summary>毎分：時刻の来た言いつけを進める。</summary>
    private void RunDueOrders() {
        if (_workspace.PrimaryFolder is not { } folder || _orders.Running is not null)
            return;
        var now = DateTime.Now;
        _ = Task.Run(() => {
            foreach (var order in _orders.Orders(folder).Where(o => o.Trigger == StandingOrder.Daily)) {
                if (order.IsDue(now, _orders.Runs(folder, order.Id, now).Last)) {
                    Dispatcher.BeginInvoke(() => StartOrder(folder, order.Id, null, automatic: true));
                    return;
                }
            }
        });
    }

    private void RunOrdersOnTrigger(string trigger, string note) {
        if (_workspace.PrimaryFolder is not { } folder || _orders.Running is not null)
            return;
        if (_orders.Orders(folder).FirstOrDefault(o => o.Trigger == trigger) is { } order)
            StartOrder(folder, order.Id, note, automatic: true);
    }

    /// <summary>言いつけを裏で進める。自動のきっかけは、1 日の回数と使用量の線の内側でだけ。</summary>
    private string? StartOrder(string folder, string orderId, string? note, bool automatic) {
        if (_orders.Running is { } running)
            return $"「{running}」を進めている最中です（同時に 1 本だけ）";
        if (automatic) {
            var order = _orders.Orders(folder).FirstOrDefault(o => o.Id == orderId);
            if (order is null || _orders.Runs(folder, orderId, DateTime.Now).Today >= order.MaxRunsPerDay)
                return "今日の回数の上限に達しています";
            var usage = new ClaudeUsageSource(new ClaudeUsageClient(), ClaudeUsageSource.DefaultStatePath()).LoadCached();
            if (usage.Usage?.FiveHour?.Percent is { } percent && percent >= OrderUsageLimitPercent) {
                ToastService.Info($"言いつけ「{orderId}」は見送りました（5 時間の使用量が {percent:0}%）");
                return "使用量の線を越えています";
            }
        }
        ToastService.Info($"言いつけ「{orderId}」を裏のワークツリーで進めます");
        _ = Task.Run(async () => {
            OrderResult result;
            try {
                result = await _orders.RunAsync(folder, orderId, note, RecordOrderMotionAsync, CancellationToken.None);
            } catch (OrderException ex) {
                Dispatcher.Invoke(() => ToastService.Error(ex.Message));
                return;
            }
            Dispatcher.Invoke(() => {
                var text = result.Status switch {
                    OrderResult.Done => $"言いつけ「{orderId}」が仕上がりました（{result.Files} ファイル）。言いつけのビューアの盆から採るか捨てます",
                    OrderResult.Empty => $"言いつけ「{orderId}」は変更なしで終わりました",
                    _ => $"言いつけ「{orderId}」は失敗しました：{Head(result.Summary)}",
                };
                if (result.Status == OrderResult.Failed) ToastService.Warning(text); else ToastService.Info(text);
                if (!IsActive && result.Status == OrderResult.Done)
                    FlashTaskbarUntilForeground();
            });
        });
        return null;

        async Task<string?> RecordOrderMotionAsync(string worktree, string resultId, CancellationToken cancel) {
            var probes = _motion.ApprovedProbes(folder);
            if (probes.Count == 0)
                return null;
            var snapshot = await _motion.RecordAllAsync(folder,
                new MotionOrigin(null, 0, $"言いつけ「{orderId}」の仕上がり", MotionSnapshot.TriggerOrder), cancel, worktree, resultId);
            if (snapshot is null)
                return null;
            var changes = MotionDiff.Compare(_motion.Store.Latest(folder), snapshot, probes,
                (owner, name) => _motion.Store.ImagePrint(folder, owner, name));
            return MotionDiff.Headline(changes) ?? "動きは同じ";
        }

        static string Head(string text) => text.Split('\n')[0] is { Length: > 120 } line ? line[..120] + "…" : text.Split('\n')[0];
    }

    // ── ビューアの口 ───────────────────────────────────────────────

    private static readonly JsonSerializerOptions ViewerJson = new(JsonSerializerDefaults.Web);

    private static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, ViewerJson);

    /// <summary><c>loomo.motion.*</c>（§44.3）。読むのは裏、Diff を開くのは UI。</summary>
    private sealed class MotionViewerApi(ShellWindow window) : IViewerApiExtension {
        public string Api => ViewerApi.Motion;

        private string Folder => window._workspace.PrimaryFolder
            ?? throw new ViewerApiException("No workspace folder is open.");

        public async Task<JsonNode?> CallAsync(string method, JsonObject? args, CancellationToken cancel) {
            var folder = Folder;
            var store = window._motion.Store;
            switch (method) {
                case "state": {
                    var probes = store.LoadProbes(folder, out var error);
                    return new JsonObject {
                        ["probes"] = new JsonArray(probes.Select(p => (JsonNode)new JsonObject {
                            ["id"] = p.Id, ["title"] = p.Title, ["kind"] = p.Kind.ToString().ToLowerInvariant(), ["everyTurn"] = p.EveryTurn,
                        }).ToArray()),
                        ["approved"] = probes.Count > 0 && store.IsApproved(folder, probes),
                        ["error"] = error,
                        ["recording"] = window._motion.IsRecording,
                    };
                }
                case "snapshots": {
                    var probes = store.LoadProbes(folder, out _);
                    var all = store.List(folder, MotionStore.KeepSnapshots);
                    var prints = new Dictionary<(string, string), string?>();
                    var rows = new JsonArray();
                    for (var i = 0; i < Math.Min(all.Count, 60); i++) {
                        var snapshot = all[i];
                        var before = all.Skip(i + 1).FirstOrDefault(other => other.OrderResultId is null);
                        var changes = MotionDiff.Compare(before, snapshot, probes, (owner, name) => {
                            if (!prints.TryGetValue((owner.Id, name), out var print))
                                prints[(owner.Id, name)] = print = store.ImagePrint(folder, owner, name);
                            return print;
                        });
                        rows.Add(new JsonObject {
                            ["id"] = snapshot.Id, ["at"] = snapshot.At.ToString("o"), ["promptHead"] = snapshot.PromptHead,
                            ["trigger"] = snapshot.Trigger, ["hasBefore"] = before is not null,
                            ["changed"] = changes.Any(c => c.Changed), ["headline"] = MotionDiff.Headline(changes),
                        });
                    }
                    return rows;
                }
                case "get": {
                    if (store.Find(folder, Text(args, "id")) is not { } snapshot)
                        return null;
                    var before = store.Previous(folder, snapshot);
                    var changes = store.Compare(folder, snapshot, store.LoadProbes(folder, out _));
                    return new JsonObject {
                        ["snapshot"] = ToNode(snapshot), ["before"] = ToNode(before), ["changes"] = ToNode(changes),
                    };
                }
                case "image": {
                    if (store.ImagePath(folder, Text(args, "id"), Text(args, "file")) is not { } path)
                        return null;
                    var bytes = await File.ReadAllBytesAsync(path, cancel);
                    if (bytes.Length > 12 * 1024 * 1024)
                        throw new ViewerApiException("The picture is too large.");
                    return "data:image/png;base64," + Convert.ToBase64String(bytes);
                }
                case "record": {
                    if (window._motion.ApprovedProbes(folder).Count == 0)
                        throw new ViewerApiException("見どころがまだ許されていません（パレット「動き：見どころを許す」）");
                    var snapshot = await window._motion.RecordAllAsync(folder,
                        new MotionOrigin(null, 0, null, MotionSnapshot.TriggerManual), cancel);
                    return snapshot?.Id;
                }
                case "openRequest": {
                    if (store.Find(folder, Text(args, "id")) is not { SessionId: { Length: > 0 } session } snapshot || snapshot.PromptLine <= 0)
                        throw new ViewerApiException("この動きには頼みごとが結ばれていません");
                    await window.Dispatcher.InvokeAsync(() => window.OpenClaudeTurnDiff(session, snapshot.PromptLine, "motion.diff"));
                    return true;
                }
                default:
                    throw new ViewerApiException($"Unknown method: motion.{method}");
            }
        }
    }

    /// <summary><c>loomo.orders.*</c>（§44.4）。採る・捨てるは git を回すので裏。差分を開くのは UI。</summary>
    private sealed class OrdersViewerApi(ShellWindow window) : IViewerApiExtension {
        public string Api => ViewerApi.Orders;

        public async Task<JsonNode?> CallAsync(string method, JsonObject? args, CancellationToken cancel) {
            var folder = window._workspace.PrimaryFolder ?? throw new ViewerApiException("No workspace folder is open.");
            var orders = window._orders;
            try {
                switch (method) {
                    case "state":
                        return new JsonObject {
                            ["running"] = orders.Running,
                            ["results"] = new JsonArray(orders.Results(folder).Select(r => (JsonNode)new JsonObject {
                                ["id"] = r.Id, ["orderId"] = r.OrderId, ["at"] = r.At.ToString("o"), ["status"] = r.Status,
                                ["files"] = r.Files, ["motion"] = r.Motion, ["summary"] = r.Summary,
                            }).ToArray()),
                            ["orders"] = new JsonArray(orders.Orders(folder).Select(o => (JsonNode)new JsonObject {
                                ["id"] = o.Id, ["prompt"] = o.Prompt, ["trigger"] = o.Trigger, ["at"] = o.At,
                            }).ToArray()),
                        };
                    case "save": {
                        var id = Text(args, "id");
                        var existing = orders.Orders(folder).FirstOrDefault(o => o.Id == id);
                        var trigger = Text(args, "trigger");
                        orders.Save(folder, new StandingOrder(id, Text(args, "prompt"), trigger,
                            trigger == StandingOrder.Daily ? Text(args, "at") : null,
                            existing?.Tools ?? StandingOrder.DefaultTools,
                            existing?.MaxRunsPerDay ?? StandingOrder.DefaultMaxRunsPerDay,
                            existing?.TimeoutMinutes ?? StandingOrder.DefaultTimeoutMinutes));
                        return true;
                    }
                    case "remove":
                        orders.Remove(folder, Text(args, "id"));
                        return true;
                    case "run": {
                        var problem = await window.Dispatcher.InvokeAsync(() => window.StartOrder(folder, Text(args, "id"), null, automatic: false));
                        if (problem is not null)
                            throw new ViewerApiException(problem);
                        return true;
                    }
                    case "take": {
                        var taken = await orders.TakeAsync(folder, Text(args, "id"), cancel);
                        _ = window.Dispatcher.BeginInvoke(() =>
                            ToastService.Info($"言いつけ「{taken.OrderId}」の {taken.Files} ファイルを作業ツリーへ当てました（コミットはしていません）"));
                        return true;
                    }
                    case "discard":
                        await orders.DiscardAsync(folder, Text(args, "id"));
                        return true;
                    case "openDiff": {
                        if (orders.Results(folder).FirstOrDefault(r => r.Id == Text(args, "id")) is not { Commit: { } commit, BaseCommit: { } baseCommit } result)
                            throw new ViewerApiException("差分を開ける仕上がりではありません");
                        await window.Dispatcher.InvokeAsync(() => window.ShowDiff(
                            new DiffOpenTarget.CommitRange(baseCommit, commit, $"言いつけ「{result.OrderId}」の仕上がり")));
                        return true;
                    }
                    default:
                        throw new ViewerApiException($"Unknown method: orders.{method}");
                }
            } catch (OrderException ex) {
                throw new ViewerApiException(ex.Message);
            }
        }
    }

    private static string Text(JsonObject? args, string name)
        => args?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
}
