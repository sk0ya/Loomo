using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Harness;

/// <summary>チケット型タスクの置き場所（§37.11）。</summary>
public enum HarnessTicketOrigin
{
    /// <summary><c>tickets/&lt;群&gt;/&lt;ID&gt;/</c>（進行中）。</summary>
    Tickets,
    /// <summary><c>meta/&lt;群&gt;/&lt;ID&gt;/</c>（ワークスペース自体の改修）。</summary>
    Meta,
    /// <summary><c>archive/&lt;テーマ&gt;/&lt;群&gt;/&lt;ID&gt;/</c>（完了済み。Run が結び付くものだけ読む）。</summary>
    Archive,
}

/// <summary>
/// チケット型タスクの定義側の値（§37.11）。<see cref="Status"/>・<see cref="Type"/>・<see cref="DependIds"/> は
/// 群の <c>tickets.tsv</c> の<b>今の</b>値、受け入れ条件の数は detail の「要件・受け入れ条件」節のチェックリスト。
/// どれも観測ではなく定義（書かれていること）で、推定は混ぜない。
/// </summary>
public sealed record HarnessLocalTicket(
    HarnessTicketOrigin Origin,
    string Group,
    string? Status,
    string? Type,
    IReadOnlyList<string> DependIds,
    int? AcceptanceTotal,
    int AcceptanceDone)
{
    /// <summary>「要件・受け入れ条件」の節があるか（節があって項目が0なら true・Total 0）。</summary>
    public bool HasAcceptanceSection => AcceptanceTotal is not null;
}

/// <summary>作業の跡の種類（§37.11.3）。</summary>
public enum HarnessTicketTraceKind
{
    /// <summary>チケットのフォルダーの中のファイル（detail・discussion・reviews）を書き換えた。</summary>
    Edited,
    /// <summary><c>tickets.tsv</c> のその行の Status を書き換えた。</summary>
    StatusChanged,
}

/// <summary>
/// Run の中でエージェントがチケットに対して実際にした書き換え1回（観測）。<see cref="Detail"/> は
/// 書き換えたファイル名、または <c>working→done</c>。
/// </summary>
public sealed record HarnessTicketTrace(HarnessTicketMention Ticket, HarnessTicketTraceKind Kind, string Detail, RunToolCall Call);

/// <summary>指示の中のチケット型タスクへの言及。<see cref="Group"/> はパスで書かれていたときだけ持つ。</summary>
public sealed record HarnessTicketMention(string Id, string? Group);

/// <summary>
/// <c>.harness/</c> を持たず、<c>tickets/&lt;群&gt;/&lt;ID&gt;/&lt;ID&gt;-detail.md</c> でチケット駆動の仕事を回している
/// ワークスペース（CurrentTask 型）を読む（§37.11）。
/// <para>設定は無く、<b>形で検出する</b>：<c>tickets/*/*/*-detail.md</c>・<c>meta/*/*/*-detail.md</c> があればそれが
/// チケット型タスク。<c>harness.yaml</c> は §37.3 のとおり <c>id:</c>・<c>version:</c> しか読まない（対象フォルダーに
/// ファイルを足させない）。<c>archive/</c> は一覧にせず、期間内の Run が言及した ID だけ読む。</para>
/// </summary>
public static class HarnessLocalTickets
{
    public const string TicketsFolder = "tickets";
    public const string MetaFolder = "meta";
    public const string ArchiveFolder = "archive";
    public const string TableFileName = "tickets.tsv";
    private const string DetailSuffix = "-detail.md";

    /// <summary>ワークフロー定義（実質 workflows）の置き場所。</summary>
    public static readonly string WorkflowSpecFolder = Path.Combine("doc", "tickets-format", "contents");
    private const string WorkflowSpecPattern = "ワークフロー仕様*.md";

    // 素の ID（VCI-001）。\b は日本語も語の文字に数えるので「VCI-001をやって」で切れない——前後は ASCII だけで見る。
    // 大文字・小文字は区別する（vci-001 は ID の書き方ではない）。
    private static readonly Regex BareId = new(@"(?<![A-Za-z0-9_-])(?<id>[A-Z]{2,4}-\d{3})(?![A-Za-z0-9_])",
        RegexOptions.CultureInvariant);

    // tickets/<群>/<ID>/… ・ meta/<群>/<ID>/… ・ archive/<テーマ>/<群>/<ID>/…（区切りは / にそろえてから見る）。
    private static readonly Regex PathMention = new(
        @"(?<![A-Za-z0-9_.-])(?:(?<root>tickets|meta)|archive/[^/\s]+)/(?<group>[^/\s]+)/(?<id>[A-Za-z]{2,4}-\d{3})(?![A-Za-z0-9_])"
        // 続きのパス（/VCI-001-detail.md）も言及の一部として覆う——ファイル名の ID を素の ID として数え直さない。
        + @"(?:/[^\s、。，．「」『』（）()<>""'`]*)?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // 書き換えた先のパス（絶対パス。区切りは / にそろえてから見る）。
    private static readonly Regex TicketFolderPath = new(
        @"(?:^|/)(?:tickets|meta|archive/[^/]+)/(?<group>[^/]+)/(?<id>[A-Za-z]{2,4}-\d{3})/",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex TableFilePath = new(
        @"(?:^|/)(?:tickets|meta|archive/[^/]+)/(?<group>[^/]+)/tickets\.tsv$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ExactId = new(@"^[A-Z]{2,4}-\d{3}$", RegexOptions.CultureInvariant);

    /// <summary>書き換えのツール。読んだだけ（Read・Grep）は跡に数えない——依存先や前例を読むのは別のチケットの仕事でも起きる。</summary>
    private static readonly HashSet<string> EditTools = new(StringComparer.Ordinal) { "Edit", "MultiEdit", "Write", "NotebookEdit" };

    private static readonly Regex AcceptanceHeading = new(@"^(?<level>#{1,6})\s*(要件\s*[・･/／]?\s*)?受け入れ条件",
        RegexOptions.CultureInvariant);

    private static readonly Regex Heading = new(@"^(?<level>#{1,6})\s", RegexOptions.CultureInvariant);

    private static readonly Regex CheckItem = new(@"^\s*[-*+]\s+\[(?<mark>[ xX])\]", RegexOptions.CultureInvariant);

    // ===== 列挙 =====

    /// <summary><c>tickets/</c> と <c>meta/</c> のチケット型タスク（archive は含めない）。</summary>
    public static IReadOnlyList<HarnessTask> ReadActive(string folder)
    {
        var result = new List<HarnessTask>();
        foreach (var (root, origin) in new[] { (TicketsFolder, HarnessTicketOrigin.Tickets), (MetaFolder, HarnessTicketOrigin.Meta) })
        {
            foreach (var groupDir in Directories(Path.Combine(folder, root)))
            {
                var group = Path.GetFileName(groupDir);
                Dictionary<string, TableRow>? table = null;
                foreach (var ticketDir in Directories(groupDir))
                {
                    foreach (var detail in DetailFiles(ticketDir))
                    {
                        table ??= ReadTable(Path.Combine(groupDir, TableFileName));
                        result.Add(ReadTicket(detail, origin, group, table));
                    }
                }
            }
        }
        return result;
    }

    /// <summary>
    /// <c>archive/</c> のうち、<paramref name="mentions"/> の ID のチケットだけを読む。群の <c>tickets.tsv</c> も
    /// 該当するチケットがある群だけ読む（archive 全体は約 90 件あり、一覧にはしない）。
    /// </summary>
    public static IReadOnlyList<HarnessTask> ReadArchived(string folder, IReadOnlyCollection<HarnessTicketMention> mentions)
    {
        var archive = Path.Combine(folder, ArchiveFolder);
        if (mentions.Count == 0 || !Directory.Exists(archive))
            return [];
        var result = new List<HarnessTask>();
        foreach (var themeDir in Directories(archive))
        {
            foreach (var groupDir in Directories(themeDir))
            {
                var group = Path.GetFileName(groupDir);
                Dictionary<string, TableRow>? table = null;
                var ids = mentions
                    .Where(m => m.Group is null || string.Equals(m.Group, group, StringComparison.OrdinalIgnoreCase))
                    .Select(m => m.Id)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var id in ids)
                {
                    var ticketDir = Path.Combine(groupDir, id);
                    if (!Directory.Exists(ticketDir))
                        continue;
                    foreach (var detail in DetailFiles(ticketDir))
                    {
                        table ??= ReadTable(Path.Combine(groupDir, TableFileName));
                        result.Add(ReadTicket(detail, HarnessTicketOrigin.Archive, group, table));
                    }
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 定義に、期間内の Run が言及した（または書き換えた）archive のチケットを足す（それ以外は読まない）。
    /// 既に定義にある（tickets/・meta/ で見つかる）言及は archive を探さない。
    /// </summary>
    public static HarnessDefinition WithArchived(HarnessDefinition definition, IEnumerable<string> folders, IEnumerable<HarnessRun> runs)
    {
        var wanted = runs.SelectMany(r => HarnessTaskLinks.TextsOf(r).SelectMany(FindMentions)
                .Concat(FindTraces(r).Select(t => t.Ticket)))
            .Distinct()
            .Where(m => !definition.Tasks.Any(t => Matches(t, m)))
            .ToList();
        if (wanted.Count == 0)
            return definition;
        var archived = folders.Where(f => !string.IsNullOrEmpty(f) && Directory.Exists(f))
            .SelectMany(f => ReadArchived(f, wanted))
            .Where(t => !definition.Tasks.Any(d => string.Equals(d.Path, t.Path, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return archived.Count == 0 ? definition : definition with { Tasks = [.. definition.Tasks, .. archived] };
    }

    /// <summary><c>doc/tickets-format/contents/ワークフロー仕様*.md</c>。</summary>
    public static IReadOnlyList<string> WorkflowSpecFiles(string folder)
    {
        var dir = Path.Combine(folder, WorkflowSpecFolder);
        if (!Directory.Exists(dir))
            return [];
        try
        {
            return Directory.GetFiles(dir, WorkflowSpecPattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // ===== 指示の中の言及 =====

    /// <summary>
    /// 文中のチケット型タスクへの言及（ID の形をしているだけの語も含む——定義にある ID と一致するかは
    /// <see cref="Matches"/> で呼び出し側が確かめる）。パスの言及は群つきで、その部分は素の ID として数え直さない。
    /// </summary>
    public static IReadOnlyList<HarnessTicketMention> FindMentions(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        var normalized = text.Replace('\\', '/');
        var result = new List<HarnessTicketMention>();
        var masked = normalized.ToCharArray();
        foreach (Match m in PathMention.Matches(normalized))
        {
            result.Add(new HarnessTicketMention(m.Groups["id"].Value.ToUpperInvariant(), m.Groups["group"].Value));
            for (var i = m.Index; i < m.Index + m.Length; i++)
                masked[i] = ' ';
        }
        // パスで群まで書かれた言及を、素の ID として別の群の同じ ID にまで広げない。
        foreach (Match m in BareId.Matches(new string(masked)))
            result.Add(new HarnessTicketMention(m.Groups["id"].Value, null));
        return result.Distinct().ToList();
    }

    /// <summary>言及がそのタスク（チケット型）を指しているか。群が書かれていれば群も一致すること。</summary>
    public static bool Matches(HarnessTask task, HarnessTicketMention mention)
        => task.LocalTicket is { } local
           && string.Equals(task.Id, mention.Id, StringComparison.OrdinalIgnoreCase)
           && (mention.Group is null || string.Equals(local.Group, mention.Group, StringComparison.OrdinalIgnoreCase));

    // ===== 作業の跡 =====

    /// <summary>
    /// Run の中でエージェント（サブエージェント含む）がチケットに対して実際にした書き換え（§37.11.3）：
    /// (1) <c>&lt;root&gt;/&lt;群&gt;/&lt;ID&gt;/</c> の中のファイルへの Edit／Write、(2) <c>tickets.tsv</c> の行の Status の書き換え
    /// （Edit の置換前後で同じ ID の行の Status が変わったもの。行の追加＝起票は数えない）。失敗・拒否された呼び出しは数えない。
    /// 定義にある ID かどうかは、ここでは見ない（<see cref="Matches"/> で呼び出し側が確かめる）。
    /// </summary>
    public static IReadOnlyList<HarnessTicketTrace> FindTraces(HarnessRun run)
    {
        var result = new List<HarnessTicketTrace>();
        foreach (var call in run.ToolCalls)
        {
            if (!EditTools.Contains(call.Tool) || call.IsError || call.IsRejected || call.FilePath is null)
                continue;
            var path = call.FilePath.Replace('\\', '/');
            if (TicketFolderPath.Match(path) is { Success: true } folder)
            {
                result.Add(new HarnessTicketTrace(
                    new HarnessTicketMention(folder.Groups["id"].Value.ToUpperInvariant(), folder.Groups["group"].Value),
                    HarnessTicketTraceKind.Edited, Path.GetFileName(path), call));
            }
            else if (TableFilePath.Match(path) is { Success: true } table && call.EditOld is { } before && call.EditNew is { } after)
            {
                var old = StatusByRow(before);
                foreach (var (id, status) in StatusByRow(after))
                {
                    if (old.TryGetValue(id, out var was) && !string.Equals(was, status, StringComparison.Ordinal))
                        result.Add(new HarnessTicketTrace(new HarnessTicketMention(id, table.Groups["group"].Value),
                            HarnessTicketTraceKind.StatusChanged, $"{was}→{status}", call));
                }
            }
        }
        return result;
    }

    /// <summary>表の断片の行ごとの Status（1列目が ID、2列目が Status——<c>tickets.tsv</c> の列の順）。</summary>
    private static Dictionary<string, string> StatusByRow(string fragment)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in fragment.Split('\n'))
        {
            var cells = line.TrimEnd('\r').Split('\t');
            if (cells.Length >= 2 && ExactId.IsMatch(cells[0].Trim()) && cells[1].Trim() is { Length: > 0 } status)
                result[cells[0].Trim()] = status;
        }
        return result;
    }

    // ===== 1件 =====

    private static HarnessTask ReadTicket(string detail, HarnessTicketOrigin origin, string group, IReadOnlyDictionary<string, TableRow> table)
    {
        var name = Path.GetFileName(detail);
        var id = name[..^DetailSuffix.Length];
        table.TryGetValue(id, out var row);
        var (total, done) = CountAcceptance(detail);
        var title = row?.Title is { Length: > 0 } t ? t : HarnessDefinitionReader.ReadTitle(detail);
        return new HarnessTask(id, title, detail, null,
            new HarnessLocalTicket(origin, group, row?.Status, row?.Type, row?.DependIds ?? [], total, done));
    }

    /// <summary>「要件・受け入れ条件」節の <c>- [ ]</c>／<c>- [x]</c> の数。節が無ければ (null, 0)。</summary>
    public static (int? Total, int Done) CountAcceptance(string detailPath)
    {
        try
        {
            int? total = null;
            var done = 0;
            var sectionLevel = 0;
            var inFence = false;
            foreach (var line in File.ReadLines(detailPath))
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    inFence = !inFence;
                    continue;
                }
                if (inFence)
                    continue;
                if (Heading.Match(line) is { Success: true } h)
                {
                    var level = h.Groups["level"].Value.Length;
                    if (sectionLevel > 0 && level <= sectionLevel)
                        sectionLevel = 0; // 節の終わり
                    if (sectionLevel == 0 && total is null && AcceptanceHeading.IsMatch(line))
                    {
                        sectionLevel = level;
                        total = 0;
                    }
                    continue;
                }
                if (sectionLevel > 0 && CheckItem.Match(line) is { Success: true } c)
                {
                    total++;
                    if (c.Groups["mark"].Value is "x" or "X")
                        done++;
                }
            }
            return (total, done);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, 0);
        }
    }

    /// <summary>detail に「要件・受け入れ条件」の見出しがあるか（点検 §37.10 の見方）。</summary>
    public static bool HasAcceptanceHeading(string detailPath) => CountAcceptance(detailPath).Total is not null;

    // ===== tickets.tsv =====

    private sealed record TableRow(string? Title, string? Status, string? Type, IReadOnlyList<string> DependIds);

    /// <summary>
    /// <c>tickets.tsv</c>（1行目が見出し。列は名前で引く：ID・Status・Type・Title・DependIDs）。
    /// 読めなければ空——題は detail の見出しに、状態は「不明」に落ちる。
    /// </summary>
    private static Dictionary<string, TableRow> ReadTable(string path)
    {
        var result = new Dictionary<string, TableRow>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return result;
        try
        {
            using var reader = new StreamReader(path);
            var header = reader.ReadLine()?.Split('\t').Select(h => h.Trim()).ToList();
            if (header is null)
                return result;
            int Col(string name) => header.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
            int idCol = Col("ID"), statusCol = Col("Status"), typeCol = Col("Type"), titleCol = Col("Title"), dependCol = Col("DependIDs");
            if (idCol < 0)
                return result;
            while (reader.ReadLine() is { } line)
            {
                var cells = line.Split('\t');
                string? Cell(int i) => i >= 0 && i < cells.Length && cells[i].Trim() is { Length: > 0 } v ? v : null;
                if (Cell(idCol) is not { } id)
                    continue;
                var depends = Cell(dependCol) is { } d
                    ? d.Split([',', ' ', '、', ';', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(x => x != "-").ToList()
                    : [];
                result[id] = new TableRow(Cell(titleCol), Cell(statusCol), Cell(typeCol), depends);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }

    // ===== ファイル =====

    private static IEnumerable<string> DetailFiles(string ticketDir)
    {
        try
        {
            return Directory.GetFiles(ticketDir, "*" + DetailSuffix)
                .Where(f => Path.GetFileName(f).Length > DetailSuffix.Length)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> Directories(string dir)
    {
        if (!Directory.Exists(dir))
            return [];
        try
        {
            return Directory.GetDirectories(dir)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
