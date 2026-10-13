using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace sk0ya.Loomo.Core.Harness;

/// <summary>改善候補に人間が付けた扱い（§37.5.1）。</summary>
public enum HarnessCandidateState
{
    /// <summary>直しをエージェントへ渡した（一括プロンプトをコピーした）。まだ確定していないので再発は数えない。</summary>
    Addressing,
    /// <summary>直した。これより後の根拠だけを「再発」として数える。</summary>
    Resolved,
    /// <summary>直さない（偶然・誤検出）。再発しても上げない。</summary>
    Dismissed,
}

public sealed record HarnessCandidateStatus(HarnessCandidateState State, DateTimeOffset At, string? Note);

/// <summary>画面での候補の位置づけ（状態と、状態を付けた後に起きたかで決まる）。</summary>
public enum HarnessCandidateStanding
{
    /// <summary>何も付いていない。</summary>
    Open,
    /// <summary>直しをエージェントへ渡した。</summary>
    Addressing,
    /// <summary>対処済みにした後は起きていない。</summary>
    Resolved,
    /// <summary>対処済みにした後にまた起きた。</summary>
    Recurred,
    /// <summary>見送った。</summary>
    Dismissed,
}

/// <summary>候補と、その位置づけ。<see cref="Candidate"/> は再発なら<b>対処後の根拠だけ</b>に絞ったもの。</summary>
public sealed record HarnessTriagedCandidate(HarnessCandidate Candidate, HarnessCandidateStanding Standing, HarnessCandidateStatus? Status)
{
    /// <summary>直す対象として並べるか（何も付いていない・再発）。</summary>
    public bool NeedsWork => Standing is HarnessCandidateStanding.Open or HarnessCandidateStanding.Recurred;
}

/// <summary>
/// 改善候補の扱い <c>.harness/runs/candidates.json</c>（§37.5.1）。候補の検出は期間内の全 Run から行うので、
/// ハーネスを直しても直す前の Run が期間に残る限り同じ候補が出続ける。ここに「いつ直したか」を持ち、
/// <see cref="Triage"/> でそれより後の根拠だけを数える。鍵は <see cref="HarnessCandidate.Id"/>（規則×対象）。
/// 書き方の規約は <see cref="HarnessVerdictStore"/> と同じ（読めないファイルは書き換えない・知らない行は残す）。
/// </summary>
public static class HarnessCandidateStore
{
    public static string PathFor(string workspaceFolder)
        => Path.Combine(workspaceFolder, HarnessDefinitionReader.HarnessFolderName, "runs", "candidates.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Dictionary<string, HarnessCandidateStatus> Load(string path)
    {
        var result = new Dictionary<string, HarnessCandidateStatus>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return result;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path), JsonOptions);
            if (raw is null)
                return result;
            foreach (var (id, entry) in raw)
            {
                if (ParseState(entry.State) is { } state)
                    result[id] = new HarnessCandidateStatus(state, entry.At ?? DateTimeOffset.MinValue, entry.Note);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return result;
    }

    /// <summary>
    /// 複数件をまとめて付ける（値が null なら外す）。書き込みは1回。ほかの候補の行はそのまま残す。
    /// 読めないときは <see cref="InvalidDataException"/> で止め、ファイルには触らない。
    /// </summary>
    public static void SetMany(string path, IReadOnlyDictionary<string, HarnessCandidateStatus?> changes)
    {
        if (changes.Count == 0)
            return;
        JsonObject root;
        if (File.Exists(path))
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(File.ReadAllText(path), documentOptions: DocumentOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{path} が JSON として読めないので書き換えない（{ex.Message}）", ex);
            }
            root = parsed as JsonObject
                ?? throw new InvalidDataException($"{path} の中身がオブジェクトではないので書き換えない");
        }
        else
        {
            root = new JsonObject();
        }

        var changed = false;
        foreach (var (id, status) in changes)
        {
            if (status is null)
            {
                changed |= root.Remove(id);
                continue;
            }
            root[id] = JsonSerializer.SerializeToNode(new Entry(FormatState(status.State), status.At, status.Note), JsonOptions);
            changed = true;
        }
        if (!changed)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// 候補に状態を当てて位置づけを決める。対処済みは<b>対処済みにした時刻より後</b>の根拠があれば再発とし、
    /// 候補をその根拠だけに絞る（頻度・推定トークンも絞った根拠から数え直す）。
    /// <para>根拠は1候補あたり <see cref="HarnessDetectors.MaxEvidencePerCandidate"/> 件までだが、新しい Run から
    /// 集めるので、対処後の根拠が上限で落ちることは実質ない。</para>
    /// </summary>
    public static IReadOnlyList<HarnessTriagedCandidate> Triage(
        IReadOnlyList<HarnessCandidate> candidates, IReadOnlyDictionary<string, HarnessCandidateStatus> statuses)
    {
        var result = new List<HarnessTriagedCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (!statuses.TryGetValue(candidate.Id, out var status))
            {
                result.Add(new(candidate, HarnessCandidateStanding.Open, null));
                continue;
            }
            if (status.State == HarnessCandidateState.Dismissed)
            {
                result.Add(new(candidate, HarnessCandidateStanding.Dismissed, status));
                continue;
            }
            // 対処中は再発を数えない：直している最中のセッション自身（記録を読む・ビルドする）が根拠を生むので、
            // 数えると渡した直後に「再発」へ戻ってしまう。数え始めは対処済みにした時刻から。
            if (status.State == HarnessCandidateState.Addressing)
            {
                result.Add(new(candidate, HarnessCandidateStanding.Addressing, status));
                continue;
            }
            var after = candidate.Evidence.Where(e => e.At is { } at && at > status.At).ToList();
            if (after.Count == 0)
            {
                result.Add(new(candidate, HarnessCandidateStanding.Resolved, status));
                continue;
            }
            var recurred = candidate with
            {
                Evidence = after,
                RunCount = after.Select(e => e.RunId).Distinct(StringComparer.Ordinal).Count(),
                EstimatedTokens = candidate.EstimatedTokens > 0 ? after.Sum(e => (long)e.Chars) / HarnessDetectors.CharsPerToken : 0,
            };
            result.Add(new(recurred, HarnessCandidateStanding.Recurred, status));
        }
        return result
            .OrderBy(t => t.NeedsWork ? 0 : 1)
            .ThenByDescending(t => t.Candidate.RunCount)
            .ThenByDescending(t => t.Candidate.EstimatedTokens)
            .ThenBy(t => t.Candidate.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static HarnessCandidateState? ParseState(string? state) => state?.ToLowerInvariant() switch
    {
        "addressing" => HarnessCandidateState.Addressing,
        "resolved" => HarnessCandidateState.Resolved,
        "dismissed" => HarnessCandidateState.Dismissed,
        _ => null,
    };

    private static string FormatState(HarnessCandidateState state) => state switch
    {
        HarnessCandidateState.Addressing => "addressing",
        HarnessCandidateState.Resolved => "resolved",
        _ => "dismissed",
    };

    private sealed record Entry(string? State, DateTimeOffset? At, string? Note);
}
