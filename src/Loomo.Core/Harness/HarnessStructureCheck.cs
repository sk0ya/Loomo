using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Harness;

/// <summary>点検1項目の状態。</summary>
public enum HarnessCheckStatus
{
    /// <summary>揃っている。</summary>
    Present,
    /// <summary>あるが、Loomo が読む最小限（ID・受け入れ条件など）が欠けている。</summary>
    Incomplete,
    /// <summary>無い。</summary>
    Missing,
}

/// <summary>点検項目の重み。必須＝無いと指標そのものが出せない／推奨＝観測から欲しくなる／任意。</summary>
public enum HarnessCheckLevel
{
    Required,
    Recommended,
    Optional,
}

/// <summary>「作る」で書き出す雛形1ファイル。<see cref="RelativePath"/> は <c>.harness/</c> からの相対。</summary>
public sealed record HarnessScaffoldFile(string RelativePath, string Content);

/// <summary>
/// 点検1項目（§37.10）。<see cref="Finding"/> は構造上の事実（何が無いか）、<see cref="Evidence"/> は記録から見た
/// 「なぜ今それが要るか」の観測事実（無ければ null）。雛形は無いファイルだけを作る（上書きしない）。
/// </summary>
public sealed record HarnessCheckItem(
    string Id,
    string Title,
    string Purpose,
    HarnessCheckLevel Level,
    HarnessCheckStatus Status,
    string Finding,
    string? Evidence,
    IReadOnlyList<HarnessScaffoldFile> Scaffold,
    string? ExistingPath)
{
    /// <summary>足りていない（作る・直す対象）。</summary>
    public bool NeedsWork => Status != HarnessCheckStatus.Present;
}

/// <summary>
/// ワークスペースフォルダー1つの <c>.harness/</c> を §37.3 の最小構造と突き合わせ、「何が無いか」を出す（§37.10）。
/// <para>判定は規則だけ（同じフォルダー・同じ記録からは同じ結果）。根拠は呼び出し側が渡すこのフォルダーの Run と
/// 改善候補から取り、雛形にも観測したコマンド（実際に通ったビルド／テスト、繰り返し失敗したコマンド）を入れる——
/// 一般論の雛形ではなく、このワークスペースで今起きていることから組む。</para>
/// </summary>
public static class HarnessStructureCheck
{
    public const string ItemManifest = "manifest";
    public const string ItemTasks = "tasks";
    public const string ItemVerdicts = "verdicts";
    public const string ItemWorkflows = "workflows";
    public const string ItemRules = "rules";
    public const string ItemEvaluations = "evaluations";
    public const string ItemAgents = "agents";

    private const int MaxListedCommands = 5;
    private const int MaxListedTickets = 5;

    private static readonly Regex AcceptanceHeading = new(
        @"^\s*#{1,6}\s*(受け入れ条件|受入条件|完了条件|acceptance)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ShellTools = new(StringComparer.Ordinal) { "Bash", "PowerShell" };

    /// <param name="folder">ワークスペースフォルダー（<c>.harness/</c> の親）。</param>
    /// <param name="runs">このフォルダーで起動した Run（期間内）。</param>
    /// <param name="candidates">改善候補（根拠がこのフォルダーの Run にあるものだけを見る）。</param>
    /// <param name="versionCount">期間内の Run に現れた版（コミット）の数。版の無い Run は数えない。</param>
    public static IReadOnlyList<HarnessCheckItem> Inspect(
        string folder, IReadOnlyList<HarnessRun> runs, IReadOnlyList<HarnessCandidate> candidates, int versionCount)
    {
        var harness = Path.Combine(folder, HarnessDefinitionReader.HarnessFolderName);
        var runIds = runs.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        // 候補は全フォルダーの Run から束ねてあるので、このフォルダーの Run の根拠だけに絞って数え直す
        // （そのままの RunCount では別フォルダーの分まで「ここで起きた」と言ってしまう）。
        var mine = candidates
            .Select(c => c.Evidence.Where(e => runIds.Contains(e.RunId)).ToList() is { Count: > 0 } evidence
                ? c with { Evidence = evidence, RunCount = evidence.Select(e => e.RunId).Distinct(StringComparer.Ordinal).Count() }
                : null)
            .OfType<HarnessCandidate>()
            .ToList();
        var tasks = TaskFiles(harness);
        // チケット型タスク（§37.11）：.harness/ が無くても tickets/・meta/ のチケットはタスクとして数える（archive は数えない）。
        var tickets = HarnessLocalTickets.ReadActive(folder);
        var buildCommands = ObservedBuildCommands(runs);

        return
        [
            CheckManifest(folder, harness),
            CheckTasks(tasks, tickets, runs),
            CheckVerdicts(harness, runs),
            CheckWorkflows(folder, harness, mine, buildCommands),
            CheckRules(harness, mine),
            CheckEvaluations(harness, [.. tasks, .. tickets], versionCount, buildCommands),
            CheckAgents(harness, runs),
        ];
    }

    /// <summary>
    /// 雛形を書き出す。<b>既にあるファイルは書かない</b>（人間が書いた中身を壊さない）。書いたファイルのパスを返す。
    /// </summary>
    public static IReadOnlyList<string> WriteScaffold(string folder, HarnessCheckItem item)
    {
        var harness = Path.Combine(folder, HarnessDefinitionReader.HarnessFolderName);
        var written = new List<string>();
        foreach (var file in item.Scaffold)
        {
            var path = Path.Combine(harness, file.RelativePath);
            if (File.Exists(path))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Content);
            written.Add(path);
        }
        return written;
    }

    // ===== 各項目 =====

    private static HarnessCheckItem CheckManifest(string folder, string harness)
    {
        var path = Path.Combine(harness, "harness.yaml");
        const string title = "harness.yaml（ID・版）";
        const string purpose = "ハーネスの識別子と版のラベル。Loomo は id: と version: の2行だけを読み、版の比較に併記する。";
        if (!File.Exists(path))
        {
            var id = Slug(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)));
            return new(ItemManifest, title, purpose, HarnessCheckLevel.Required, HarnessCheckStatus.Missing,
                Directory.Exists(harness) ? ".harness/harness.yaml が無い。" : ".harness/ が無い。",
                null,
                [new("harness.yaml",
                    $"id: {id}\nversion: 0.1.0\n# Loomo が読むのは id: と version: の2行だけ（§37.3）。他のキーは自由に足してよい。\n")],
                null);
        }
        var (manifestId, version) = HarnessDefinitionReader.ReadManifest(path);
        var lacks = new List<string>();
        if (manifestId is null) lacks.Add("id:");
        if (version is null) lacks.Add("version:");
        return lacks.Count == 0
            ? new(ItemManifest, title, purpose, HarnessCheckLevel.Required, HarnessCheckStatus.Present,
                $"id: {manifestId} ／ version: {version}", null, [], path)
            : new(ItemManifest, title, purpose, HarnessCheckLevel.Required, HarnessCheckStatus.Incomplete,
                $"{string.Join(" と ", lacks)} の行が無い（行頭に書く）。", null, [], path);
    }

    private static HarnessCheckItem CheckTasks(
        IReadOnlyList<HarnessTask> tasks, IReadOnlyList<HarnessTask> tickets, IReadOnlyList<HarnessRun> runs)
    {
        const string title = "tasks/（タスク定義）";
        const string purpose = "1ファイル1タスク（目的・受け入れ条件）。指示に task:<id> かファイルのパスを含めた Run がそのタスクに結び付く（§37.3.1）。"
            + "チケット（AB#1234・Work Item / Issue の URL）を書いた Run はチケットに結び付く（§37.3.2）。"
            + "tickets/<群>/<ID>/<ID>-detail.md のチケット型タスクは、指示に ID（VCI-001）かそのパスを書いた Run が結び付く（§37.11）。";
        var all = tasks.Concat(tickets).ToList();
        var links = runs.Select(r => HarnessTaskLinks.Links(r, all)).ToList();
        var linked = links.Count(l => l.Count > 0);
        // 指示には無く、チケットの書き換え（作業の跡）だけで結んだ Run。指示に ID を書く習慣の有無が分かる。
        var traceOnly = links.Count(l => l.Count > 0 && l.All(x => x.Basis == HarnessTaskLinkBasis.Observed));
        var ticketOnly = links.SelectMany(l => l).Select(l => l.Task).Where(t => t.IsTicketOnly).Select(t => t.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var evidence = runs.Count == 0 ? null
            : $"期間内の Run {runs.Count} 件のうち、タスクに結び付いたのは {linked} 件"
              + (traceOnly > 0 ? $"（うち指示に ID が無く作業の跡で結んだもの {traceOnly} 件）" : "")
              + (ticketOnly.Count > 0 ? $"（タスクファイルの無いチケット {ticketOnly.Count} 件: {ListIds(ticketOnly)}）。" : "。");
        if (all.Count == 0 && ticketOnly.Count > 0)
        {
            // チケットで仕事を回している：Run は束ねられているので「無い」とは言わない。受け入れ条件はチケット側にある前提。
            return new(ItemTasks, title, purpose, HarnessCheckLevel.Required, HarnessCheckStatus.Present,
                "タスクファイルは無いが、Run はチケットに結び付いている（チケットをタスクとして扱う）。"
                + "ハーネス側で受け入れ条件を持ちたいチケットだけ、ticket: を書いたタスクファイルを作る。",
                evidence, [], null);
        }
        if (all.Count == 0)
        {
            return new(ItemTasks, title, purpose, HarnessCheckLevel.Required, HarnessCheckStatus.Missing,
                "タスクが1つも無い。Run が何の仕事の試行なのかを記録できない。", evidence,
                [new(Path.Combine("tasks", "new-task.md"), TaskTemplate("new-task"))], null);
        }
        var counts = tickets.Count == 0
            ? $"タスク {tasks.Count} 件"
            : $"{(tasks.Count > 0 ? $"タスク {tasks.Count} 件・" : "")}チケット型タスク {tickets.Count} 件"
              + $"（tickets/ {tickets.Count(t => t.LocalTicket?.Origin == HarnessTicketOrigin.Tickets)}・meta/ {tickets.Count(t => t.LocalTicket?.Origin == HarnessTicketOrigin.Meta)}）";
        // チケット型は detail の「要件・受け入れ条件」、タスクファイルは「受け入れ条件」の見出しを見る。
        var withoutAcceptance = all.Where(t => t.LocalTicket is { } local ? !local.HasAcceptanceSection : t.Path is { } path && !HasAcceptance(path)).ToList();
        if (withoutAcceptance.Count > 0)
        {
            var heading = withoutAcceptance.All(t => t.LocalTicket is not null) ? "「## 要件・受け入れ条件」" : "「## 受け入れ条件」";
            return new(ItemTasks, title, purpose, HarnessCheckLevel.Required, HarnessCheckStatus.Incomplete,
                $"{counts}のうち、受け入れ条件の節が無い: {ListIds(withoutAcceptance.Select(t => t.Id).ToList())}"
                + $"（{heading}を足す。エージェントの自己申告だけで完了にしないため）",
                evidence, [], withoutAcceptance[0].Path);
        }
        return new(ItemTasks, title, purpose, HarnessCheckLevel.Required, HarnessCheckStatus.Present,
            $"{counts}（すべて受け入れ条件あり）。"
            + (runs.Count > 0 && linked == 0
                ? tickets.Count > 0
                    ? " ただし期間内に結び付いた Run が無い——指示にチケットの ID（例 " + tickets[0].Id + "）を含める。"
                    : " ただし期間内に結び付いた Run が無い——指示に task:<id> かチケット（AB#1234）を含める。"
                : ""),
            evidence, [], null);
    }

    private static string ListIds(IReadOnlyList<string> ids)
        => string.Join(", ", ids.Take(MaxListedTickets)) + (ids.Count > MaxListedTickets ? $" ほか {ids.Count - MaxListedTickets} 件" : "");

    private static HarnessCheckItem CheckVerdicts(string harness, IReadOnlyList<HarnessRun> runs)
    {
        const string title = "runs/verdicts.json（人間の判定）";
        const string purpose = "Run の受け入れ／差し戻し。これが無いと「受け入れ1件あたりトークン」も版の良し悪しも出せない。";
        var path = Path.Combine(harness, "runs", "verdicts.json");
        var verdicts = HarnessVerdictStore.Load(path);
        var judged = runs.Count(r => verdicts.ContainsKey(r.Id));
        var evidence = runs.Count == 0 ? null : $"期間内の Run {runs.Count} 件のうち、判定済み {judged} 件。";
        // 雛形は作らない——判定は実行タブで Run に付けるもの（空のファイルを作っても意味が無い）。
        return verdicts.Count == 0
            ? new(ItemVerdicts, title, purpose, HarnessCheckLevel.Required, HarnessCheckStatus.Missing,
                "判定が1件も無い。「実行」タブで Run に受け入れ／差し戻しを付けると作られる。", evidence, [], null)
            : new(ItemVerdicts, title, purpose, HarnessCheckLevel.Required, HarnessCheckStatus.Present,
                $"判定 {verdicts.Count} 件。", evidence, [], path);
    }

    private static HarnessCheckItem CheckWorkflows(
        string folder, string harness, IReadOnlyList<HarnessCandidate> candidates, IReadOnlyList<(string Command, int Runs)> buildCommands)
    {
        const string title = "workflows/（工程・検証フロー）";
        const string purpose = "完了までの工程。特に「変更したらビルド／テストで確かめてから終える」を書いておく場所。";
        var unverified = candidates.Where(c => c.RuleId == HarnessDetectors.RuleUnverified).Sum(c => c.RunCount);
        var evidence = unverified > 0 ? $"検証なしの変更が {unverified} Run で起きている（改善候補）。" : null;
        if (HasFiles(Path.Combine(harness, "workflows")))
            return Present(ItemWorkflows, title, purpose, HarnessCheckLevel.Recommended, harness, "workflows", evidence);
        // チケット駆動のワークスペースの工程定義（§37.11）。同じ役割なので workflows/ があるのと同じに扱う。
        if (HarnessLocalTickets.WorkflowSpecFiles(folder) is { Count: > 0 } specs)
            return new(ItemWorkflows, title, purpose, HarnessCheckLevel.Recommended, HarnessCheckStatus.Present,
                $"{specs.Count} 件（{HarnessLocalTickets.WorkflowSpecFolder.Replace('\\', '/')}）: "
                + $"{string.Join(", ", specs.Take(MaxListedCommands).Select(Path.GetFileName))}{(specs.Count > MaxListedCommands ? " …" : "")}",
                evidence, [], specs[0]);

        var sb = new StringBuilder();
        sb.AppendLine("# 変更したら確かめてから終える");
        sb.AppendLine();
        sb.AppendLine("## 工程");
        sb.AppendLine();
        sb.AppendLine("1. 変更する前に、対象の範囲を読み直す。");
        sb.AppendLine("2. 変更する。");
        sb.AppendLine("3. ビルド／テストを走らせ、失敗があれば直してからもう一度走らせる。");
        sb.AppendLine("4. 通ったことを報告に書く（走らせていないなら「未検証」と書く）。");
        sb.AppendLine();
        sb.AppendLine("## 検証のコマンド");
        sb.AppendLine();
        AppendCommands(sb, buildCommands, "（期間内の記録に、通ったビルド／テストのコマンドが無かった。ここに書く）");
        return new(ItemWorkflows, title, purpose, HarnessCheckLevel.Recommended, HarnessCheckStatus.Missing,
            "工程の定義が無い。", evidence,
            [new(Path.Combine("workflows", "verify-before-done.md"), sb.ToString())], null);
    }

    private static HarnessCheckItem CheckRules(string harness, IReadOnlyList<HarnessCandidate> candidates)
    {
        const string title = "rules/（開発ルール・制約）";
        const string purpose = "繰り返し踏む失敗を、毎回の試行錯誤ではなくルールとして先に渡す場所。";
        var retries = candidates.Where(c => c.RuleId == HarnessDetectors.RuleRetry).ToList();
        var mismatches = candidates.Where(c => c.RuleId == HarnessDetectors.RuleEditMismatch).Sum(c => c.RunCount);
        var parts = new List<string>();
        if (retries.Count > 0) parts.Add($"同じコマンドの失敗反復 {retries.Count} 種（{retries.Sum(c => c.RunCount)} Run）");
        if (mismatches > 0) parts.Add($"編集の空振り {mismatches} Run");
        var evidence = parts.Count > 0 ? string.Join("・", parts) + "（改善候補）。" : null;
        if (HasFiles(Path.Combine(harness, "rules")))
            return Present(ItemRules, title, purpose, HarnessCheckLevel.Recommended, harness, "rules", evidence);

        var sb = new StringBuilder();
        sb.AppendLine("# 既知の失敗と、その避け方");
        sb.AppendLine();
        if (retries.Count > 0)
        {
            sb.AppendLine("## 繰り返し失敗したコマンド（観測）");
            sb.AppendLine();
            foreach (var c in retries.OrderByDescending(c => c.RunCount).Take(MaxListedCommands))
            {
                var e = c.Evidence.FirstOrDefault();
                sb.AppendLine($"- `{c.Key}`（{c.RunCount} Run）{(e is null ? "" : $"例: `{e.Summary}`")}");
                sb.AppendLine("  - 避け方: ");
            }
            sb.AppendLine();
        }
        if (mismatches > 0)
        {
            sb.AppendLine("## 編集");
            sb.AppendLine();
            sb.AppendLine("- 置換する前に、対象の範囲をその場で読み直す（記憶の中の文字列で置換しない）。");
            sb.AppendLine();
        }
        sb.AppendLine("## ルール");
        sb.AppendLine();
        sb.AppendLine("- ");
        return new(ItemRules, title, purpose, HarnessCheckLevel.Recommended, HarnessCheckStatus.Missing,
            "ルールの定義が無い。", evidence,
            [new(Path.Combine("rules", "known-failures.md"), sb.ToString())], null);
    }

    private static HarnessCheckItem CheckEvaluations(
        string harness, IReadOnlyList<HarnessTask> tasks, int versionCount, IReadOnlyList<(string Command, int Runs)> buildCommands)
    {
        const string title = "evaluations/（評価シナリオ）";
        const string purpose = "新旧の版に同じ課題を与えて比べるための固定の評価。単発の良い結果で改善と判断しないため（構想§10）。";
        var evidence = versionCount >= 2
            ? $"期間内の Run に版が {versionCount} 個ある——同じ課題で比べないと版の差か課題の差か分からない。"
            : null;
        if (HasFiles(Path.Combine(harness, "evaluations")))
            return Present(ItemEvaluations, title, purpose, HarnessCheckLevel.Recommended, harness, "evaluations", evidence);

        var sb = new StringBuilder();
        sb.AppendLine("# 基準の評価");
        sb.AppendLine();
        sb.AppendLine("## 対象タスク");
        sb.AppendLine();
        if (tasks.Count > 0)
            foreach (var t in tasks.Take(MaxListedCommands))
                sb.AppendLine(t.LocalTicket is null ? $"- task:{t.Id}（{t.Title}）" : $"- {t.Id}（{t.Title}）");
        else
            sb.AppendLine("- task:<id>");
        sb.AppendLine();
        sb.AppendLine("## 検証条件");
        sb.AppendLine();
        AppendCommands(sb, buildCommands, "- （ビルド／テストのコマンド）");
        sb.AppendLine();
        sb.AppendLine("## 判定");
        sb.AppendLine();
        sb.AppendLine("- 版ごとに同じタスクを3回以上走らせ、ハーネスペイン「版の比較」で受け入れ1件あたりトークンとツール失敗率を見る。");
        return new(ItemEvaluations, title, purpose, HarnessCheckLevel.Recommended, HarnessCheckStatus.Missing,
            "評価の定義が無い。", evidence,
            [new(Path.Combine("evaluations", "baseline.md"), sb.ToString())], null);
    }

    private static HarnessCheckItem CheckAgents(string harness, IReadOnlyList<HarnessRun> runs)
    {
        const string title = "agents/（役割・設定）";
        const string purpose = "エージェントの役割・モデルの使い分け。サブエージェントを使うなら、その役割をここに書く。";
        var used = runs.SelectMany(r => r.AgentsUsed).GroupBy(a => a, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ToList();
        var models = runs.SelectMany(r => r.Models).Distinct(StringComparer.Ordinal).ToList();
        var evidence = used.Count > 0
            ? $"サブエージェントの使用: {string.Join(", ", used.Take(MaxListedCommands).Select(g => $"{g.Key} {g.Count()}回"))}。"
            : null;
        if (HasFiles(Path.Combine(harness, "agents")))
            return Present(ItemAgents, title, purpose, HarnessCheckLevel.Optional, harness, "agents", evidence);

        var sb = new StringBuilder();
        sb.AppendLine("# 主エージェント");
        sb.AppendLine();
        sb.AppendLine("## 役割");
        sb.AppendLine();
        sb.AppendLine("- ");
        sb.AppendLine();
        sb.AppendLine("## モデル");
        sb.AppendLine();
        sb.AppendLine(models.Count > 0 ? $"- 観測: {string.Join(", ", models)}" : "- ");
        if (used.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## サブエージェント（観測）");
            sb.AppendLine();
            foreach (var g in used.Take(MaxListedCommands))
                sb.AppendLine($"- {g.Key}（{g.Count()}回）: 役割 ");
        }
        return new(ItemAgents, title, purpose, HarnessCheckLevel.Optional, HarnessCheckStatus.Missing,
            "役割の定義が無い（任意）。", evidence,
            [new(Path.Combine("agents", "main.md"), sb.ToString())], null);
    }

    // ===== 共通 =====

    private static HarnessCheckItem Present(
        string id, string title, string purpose, HarnessCheckLevel level, string harness, string sub, string? evidence)
    {
        var dir = Path.Combine(harness, sub);
        var files = SafeFiles(dir);
        return new(id, title, purpose, level, HarnessCheckStatus.Present,
            $"{files.Count} 件: {string.Join(", ", files.Take(MaxListedCommands).Select(Path.GetFileName))}{(files.Count > MaxListedCommands ? " …" : "")}",
            evidence, [], files.FirstOrDefault());
    }

    private static string TaskTemplate(string id) =>
        "# （タスクの題）\n\n"
        + "ticket: \n\n"
        + "## 目的\n\n- \n\n"
        + "## 受け入れ条件\n\n- [ ] \n\n"
        + "## 参照\n\n- \n\n"
        + $"<!-- 実行するときは指示に task:{id} かこのファイルのパスを含める（Run と結び付く、§37.3.1）。ID＝ファイル名。\n"
        + "     上の ticket: の行にチケット（AB#1234・Work Item / Issue の URL・番号だけ）を書くと、指示にそのチケットを\n"
        + "     書いた Run もこのタスクに結び付く（§37.3.2）。使わなければ行ごと消してよい。 -->\n";

    private static void AppendCommands(StringBuilder sb, IReadOnlyList<(string Command, int Runs)> commands, string fallback)
    {
        if (commands.Count == 0)
        {
            sb.AppendLine(fallback);
            return;
        }
        foreach (var (command, count) in commands.Take(MaxListedCommands))
            sb.AppendLine($"- `{command}`（観測: {count} Run で成功）");
    }

    /// <summary>期間内に<b>成功した</b>ビルド／テストのコマンド（1行目・<c>cd x &amp;&amp;</c> の前置きは外す）を、使われた Run 数の多い順に。</summary>
    public static IReadOnlyList<(string Command, int Runs)> ObservedBuildCommands(IReadOnlyList<HarnessRun> runs)
    {
        var counts = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            foreach (var call in run.ToolCalls)
            {
                if (!ShellTools.Contains(call.Tool) || call.IsError || call.IsRejected || !call.HasResult
                    || !HarnessDetectors.IsBuildOrTest(call.Command))
                    continue;
                var command = CleanCommand(call.Command!);
                if (command.Length == 0)
                    continue;
                if (!counts.TryGetValue(command, out var set))
                    counts[command] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(run.Id);
            }
        }
        return counts.Select(kv => (kv.Key, kv.Value.Count))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Key, StringComparer.Ordinal)
            .ToList();
    }

    private static string CleanCommand(string command)
    {
        var line = command.Split('\n')[0].Trim();
        var chained = Regex.Match(line, @"^cd\s+\S+\s*(&&|;)\s*(?<rest>.+)$");
        if (chained.Success)
            line = chained.Groups["rest"].Value.Trim();
        // 出力の絞り込み（| tail 等）は検証の中身ではないので外す。
        var pipe = line.IndexOf(" | ", StringComparison.Ordinal);
        if (pipe > 0)
            line = line[..pipe].Trim();
        return Regex.Replace(line, @"\s*2>&1$", "");
    }

    private static IReadOnlyList<HarnessTask> TaskFiles(string harness)
        => SafeFiles(Path.Combine(harness, "tasks"))
            .Select(HarnessDefinitionReader.ReadTask)
            .ToList();

    private static bool HasAcceptance(string path)
    {
        try { return AcceptanceHeading.IsMatch(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; } // 読めないものを欠けとは言わない
    }

    private static bool HasFiles(string dir) => SafeFiles(dir).Count > 0;

    /// <summary>直下のファイル（<c>.</c> 始まりは除く——<see cref="HarnessDefinitionReader"/> と同じ数え方）。</summary>
    private static IReadOnlyList<string> SafeFiles(string dir)
    {
        if (!Directory.Exists(dir))
            return [];
        try
        {
            return Directory.GetFiles(dir)
                .Where(f => !Path.GetFileName(f).StartsWith('.'))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string Slug(string name)
    {
        var slug = Regex.Replace(name.ToLower(CultureInfo.InvariantCulture), @"[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "harness" : slug;
    }
}
