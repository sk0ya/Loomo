using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace sk0ya.Loomo.Core.Motion;

/// <summary>見どころの種類（§44.3）。</summary>
public enum MotionProbeKind
{
    /// <summary>決まったコマンドの出力と終了コード。</summary>
    Command,
    /// <summary>テストの成否の一覧（コマンドが <c>$env:LOOMO_OUT</c> に書いた TRX／JUnit の XML を読む）。</summary>
    Tests,
    /// <summary>画面の絵（コマンドが <c>$env:LOOMO_OUT</c> に書いた PNG）。</summary>
    Image,
}

/// <summary>
/// 見どころ 1 つ（§44.3）＝「何の動きを見るか」。決まった PowerShell の行を裏で回し、その出力・テストの成否・絵を録る。
/// </summary>
/// <param name="EveryTurn">Claude のターンが終わるたびに回すか。false なら手で（または言いつけ＝§44.4 から）。</param>
/// <param name="Ignore">比べる前に出力から落とす行（正規表現）。所要時間や日時のように毎回変わる行を「動きが変わった」に数えない。</param>
public sealed record MotionProbe(
    string Id, string Title, MotionProbeKind Kind, string Command, int TimeoutSeconds, bool EveryTurn, IReadOnlyList<string> Ignore)
{
    public const int DefaultTimeoutSeconds = 120;
    public const int MaxTimeoutSeconds = 1800;
}

/// <summary>見どころの定義（<c>probes.json</c>）の読み方と、許しの指紋。I/O なし。</summary>
public static class MotionProbes
{
    private static readonly Regex IdPattern = new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,39}$", RegexOptions.CultureInvariant);

    /// <summary>読めなければ null と、書いた人がそのまま直せる英語の理由。</summary>
    public static IReadOnlyList<MotionProbe>? Parse(string json, out string? error)
    {
        error = null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("probes", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                error = "probes.json must be { \"probes\": [ ... ] }.";
                return null;
            }
            var probes = new List<MotionProbe>();
            foreach (var item in list.EnumerateArray())
            {
                var id = Text(item, "id") ?? "";
                if (!IdPattern.IsMatch(id) || probes.Any(p => p.Id == id))
                {
                    error = $"Probe id \"{id}\" is invalid or duplicated (letters, digits, _ . -; max 40).";
                    return null;
                }
                var kindText = Text(item, "kind") ?? "command";
                MotionProbeKind kind;
                switch (kindText)
                {
                    case "command": kind = MotionProbeKind.Command; break;
                    case "tests": kind = MotionProbeKind.Tests; break;
                    case "image": kind = MotionProbeKind.Image; break;
                    default:
                        error = $"Probe \"{id}\": kind must be \"command\", \"tests\" or \"image\".";
                        return null;
                }
                var command = Text(item, "command");
                if (string.IsNullOrWhiteSpace(command))
                {
                    error = $"Probe \"{id}\" needs a non-empty \"command\" (a PowerShell command line).";
                    return null;
                }
                var when = Text(item, "when") ?? "turn";
                if (when is not ("turn" or "manual"))
                {
                    error = $"Probe \"{id}\": when must be \"turn\" or \"manual\".";
                    return null;
                }
                var timeout = item.TryGetProperty("timeoutSeconds", out var seconds) && seconds.TryGetInt32(out var n)
                    ? Math.Clamp(n, 1, MotionProbe.MaxTimeoutSeconds) : MotionProbe.DefaultTimeoutSeconds;
                var ignore = new List<string>();
                if (item.TryGetProperty("ignore", out var ignoreNode) && ignoreNode.ValueKind == JsonValueKind.Array)
                {
                    foreach (var pattern in ignoreNode.EnumerateArray())
                    {
                        if (pattern.GetString() is not { Length: > 0 } text)
                            continue;
                        try { _ = new Regex(text); }
                        catch (ArgumentException ex)
                        {
                            error = $"Probe \"{id}\": invalid ignore pattern \"{text}\": {ex.Message}";
                            return null;
                        }
                        ignore.Add(text);
                    }
                }
                probes.Add(new MotionProbe(id, Text(item, "title") is { Length: > 0 } title ? title : id,
                    kind, command!, timeout, when == "turn", ignore));
            }
            return probes;
        }
        catch (JsonException ex)
        {
            error = "probes.json is not valid JSON: " + ex.Message;
            return null;
        }
        catch (InvalidOperationException ex)
        {
            error = "probes.json has an unexpected shape: " + ex.Message;
            return null;
        }
    }

    /// <summary>許しの単位＝回る行。題・無視する行・いつ回すかを変えても聞き直さないが、行が変わったら聞き直す。</summary>
    public static string Fingerprint(IEnumerable<MotionProbe> probes)
    {
        var text = new StringBuilder();
        foreach (var probe in probes.OrderBy(p => p.Id, StringComparer.Ordinal))
            text.Append(probe.Id).Append('\0').Append(probe.Command).Append('\0').Append(probe.TimeoutSeconds).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>テスト 1 件の成否。<see cref="Outcome"/> は <c>passed</c>／<c>failed</c>／<c>skipped</c>。</summary>
public sealed record MotionTest(string Name, string Outcome)
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
}

/// <summary>見どころ 1 つを 1 回回した結果。</summary>
/// <param name="Images">絵のファイル名（スナップショットのフォルダーの中）。</param>
public sealed record MotionSample(
    string ProbeId, string Title, MotionProbeKind Kind, int ExitCode, bool TimedOut, string Output,
    IReadOnlyList<MotionTest> Tests, IReadOnlyList<string> Images, long DurationMs);

/// <summary>ある時点の動き（§44.3）。頼みごとの鍵は §43 と同じ（セッションと頼みごとの行）。</summary>
/// <param name="Trigger"><c>turn</c>（ターンの終わり）・<c>manual</c>（手で）・<c>order</c>（言いつけ）。</param>
public sealed record MotionSnapshot(
    string Id, DateTimeOffset At, string? SessionId, int PromptLine, string? PromptHead, string Trigger,
    IReadOnlyList<MotionSample> Samples)
{
    public const string TriggerTurn = "turn";
    public const string TriggerManual = "manual";
    /// <summary>Claude のターンが終わる直前（Stop フック）に録ったもの。「壊れたら直させる」（§45.6）の判定に使う。
    /// 添える知らせの元にはしない（判定の結果を Stop の返事で直接伝えるため）。</summary>
    public const string TriggerStop = "stop";
    public const string TriggerOrder = "order";

    /// <summary>録ったときの作業ツリーの印（HEAD と変更のあるファイル）。同じなら、ターンの終わりに録り直さない。</summary>
    public string? Stamp { get; init; }

    /// <summary>言いつけの仕上がり（裏のワークツリー）で録ったものか。ほかの時点の「前」にはしない。</summary>
    public string? OrderResultId { get; init; }
}

/// <summary>テストの結果のファイル（TRX・JUnit の XML）から成否の一覧を取り出す。I/O なし。</summary>
public static class MotionTestResults
{
    public static IReadOnlyList<MotionTest> Parse(string xml)
    {
        try
        {
            var root = XDocument.Parse(xml).Root;
            if (root is null)
                return [];
            return root.Name.LocalName == "TestRun" ? ParseTrx(root) : ParseJUnit(root);
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }

    private static IReadOnlyList<MotionTest> ParseTrx(XElement root)
        => root.Descendants().Where(e => e.Name.LocalName == "UnitTestResult")
            .Select(e => new MotionTest((string?)e.Attribute("testName") ?? "", ((string?)e.Attribute("outcome") ?? "") switch
            {
                "Passed" => MotionTest.Passed,
                "Failed" or "Error" or "Timeout" or "Aborted" => MotionTest.Failed,
                _ => MotionTest.Skipped,
            }))
            .Where(test => test.Name.Length > 0).ToArray();

    private static IReadOnlyList<MotionTest> ParseJUnit(XElement root)
        => root.DescendantsAndSelf().Where(e => e.Name.LocalName == "testcase")
            .Select(e =>
            {
                var name = (string?)e.Attribute("name") ?? "";
                var owner = (string?)e.Attribute("classname");
                var outcome = e.Elements().Any(c => c.Name.LocalName is "failure" or "error") ? MotionTest.Failed
                    : e.Elements().Any(c => c.Name.LocalName == "skipped") ? MotionTest.Skipped
                    : MotionTest.Passed;
                return new MotionTest(string.IsNullOrEmpty(owner) ? name : owner + "." + name, outcome);
            })
            .Where(test => test.Name.Length > 0).ToArray();
}

/// <summary>テスト 1 件の入れ替わり。<see cref="Before"/>／<see cref="After"/> が null なら、その側には無かった。</summary>
public sealed record MotionTestFlip(string Name, string? Before, string? After);

/// <summary>見どころ 1 つの、前と後の違い。</summary>
/// <param name="HasBefore">前の動きがあるか（録り始める前・見どころを足す前は無い）。</param>
public sealed record MotionChange(
    string ProbeId, string Title, MotionProbeKind Kind, bool HasBefore, bool Changed, string Summary,
    int? ExitBefore, int ExitAfter, bool OutputChanged, IReadOnlyList<MotionTestFlip> Flips, IReadOnlyList<string> ImagesChanged);

/// <summary>2 つの時点の動きを比べる（§44.3）。絵は中身の指紋で比べるので、呼ぶ側が指紋の引き方を渡す。I/O なし。</summary>
public static class MotionDiff
{
    /// <param name="imagePrint">（スナップショット, 絵のファイル名）→ 中身の指紋。読めなければ null。</param>
    public static IReadOnlyList<MotionChange> Compare(
        MotionSnapshot? before, MotionSnapshot after, IReadOnlyList<MotionProbe> probes,
        Func<MotionSnapshot, string, string?>? imagePrint = null)
    {
        var changes = new List<MotionChange>();
        foreach (var sample in after.Samples)
        {
            var previous = before?.Samples.FirstOrDefault(s => s.ProbeId == sample.ProbeId);
            if (previous is null)
            {
                changes.Add(new MotionChange(sample.ProbeId, sample.Title, sample.Kind, false, false, "前の動きはまだありません",
                    null, sample.ExitCode, false, [], []));
                continue;
            }
            var ignore = probes.FirstOrDefault(p => p.Id == sample.ProbeId)?.Ignore ?? [];
            var outputChanged = Normalize(previous.Output, ignore) != Normalize(sample.Output, ignore);
            var flips = Flips(previous.Tests, sample.Tests);
            var images = sample.Images.Union(previous.Images)
                .Where(name => !previous.Images.Contains(name) || !sample.Images.Contains(name)
                               || imagePrint is null || imagePrint(before!, name) != imagePrint(after, name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
            var exitChanged = previous.ExitCode != sample.ExitCode;
            var changed = sample.Kind switch
            {
                MotionProbeKind.Tests => flips.Count > 0 || (sample.Tests.Count == 0 && (exitChanged || outputChanged)),
                MotionProbeKind.Image => images.Length > 0 || exitChanged,
                _ => outputChanged || exitChanged,
            };
            changes.Add(new MotionChange(sample.ProbeId, sample.Title, sample.Kind, true, changed,
                changed ? Describe(sample, previous, flips, images, outputChanged) : "動きは同じ",
                previous.ExitCode, sample.ExitCode, outputChanged, flips, images));
        }
        return changes;
    }

    /// <summary>悪い方へ変わったか（テストが落ちるようになった・終了コードが 0 から外れた）。言いつけのきっかけ（§44.4）。</summary>
    public static bool Worsened(IReadOnlyList<MotionChange> changes)
        => changes.Any(change => change.HasBefore
            && (change.Flips.Any(flip => flip.After == MotionTest.Failed)
                || (change.ExitBefore == 0 && change.ExitAfter != 0)));

    /// <summary>全体を 1 行で（通知・一覧の添え書き）。変わっていなければ null。</summary>
    public static string? Headline(IReadOnlyList<MotionChange> changes)
    {
        var changed = changes.Where(change => change.Changed).ToArray();
        return changed.Length == 0 ? null : string.Join("／", changed.Select(change => $"{change.Title}：{change.Summary}"));
    }

    private static string Describe(MotionSample after, MotionSample before, IReadOnlyList<MotionTestFlip> flips,
        IReadOnlyList<string> images, bool outputChanged)
    {
        var parts = new List<string>();
        if (after.Kind == MotionProbeKind.Tests && flips.Count > 0)
        {
            var broke = flips.Count(f => f.After == MotionTest.Failed);
            var fixedCount = flips.Count(f => f.Before == MotionTest.Failed && f.After == MotionTest.Passed);
            var added = flips.Count(f => f.Before is null && f.After != MotionTest.Failed);
            var removed = flips.Count(f => f.After is null);
            if (broke > 0) parts.Add($"{broke} 件が落ちるようになった");
            if (fixedCount > 0) parts.Add($"{fixedCount} 件が通るようになった");
            if (added > 0) parts.Add($"{added} 件増えた");
            if (removed > 0) parts.Add($"{removed} 件無くなった");
            if (parts.Count == 0) parts.Add($"{flips.Count} 件の成否が変わった");
        }
        else if (after.Kind == MotionProbeKind.Image && images.Count > 0)
            parts.Add($"絵 {images.Count} 枚が変わった");
        else if (outputChanged)
            parts.Add("出力が変わった");
        if (before.ExitCode != after.ExitCode)
            parts.Add($"終了コード {before.ExitCode} → {after.ExitCode}");
        return string.Join("・", parts);
    }

    private static IReadOnlyList<MotionTestFlip> Flips(IReadOnlyList<MotionTest> before, IReadOnlyList<MotionTest> after)
    {
        var old = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var test in before)
            old[test.Name] = test.Outcome;
        var now = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var test in after)
            now[test.Name] = test.Outcome;
        var flips = new List<MotionTestFlip>();
        foreach (var (name, outcome) in now)
        {
            if (!old.TryGetValue(name, out var was))
                flips.Add(new MotionTestFlip(name, null, outcome));
            else if (was != outcome)
                flips.Add(new MotionTestFlip(name, was, outcome));
        }
        foreach (var (name, was) in old)
        {
            if (!now.ContainsKey(name))
                flips.Add(new MotionTestFlip(name, was, null));
        }
        // 落ちるようになったものを先に。
        return flips.OrderBy(f => f.After == MotionTest.Failed ? 0 : 1).ThenBy(f => f.Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>比べる前の出力：行末の空白を落とし、無視する行を抜く。</summary>
    public static string Normalize(string output, IReadOnlyList<string> ignore)
    {
        var patterns = ignore.Select(pattern => new Regex(pattern, RegexOptions.CultureInvariant)).ToArray();
        return string.Join("\n", output.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => !patterns.Any(pattern => pattern.IsMatch(line)))).TrimEnd('\n');
    }
}
