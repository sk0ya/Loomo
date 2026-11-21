using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Viewers;
using sk0ya.Loomo.Core.Motion;
using sk0ya.Loomo.Core.Processes;
using sk0ya.Loomo.Services.Viewers;

namespace sk0ya.Loomo.Services.Motion;

/// <summary>
/// 動きの置き場（§44.3）。ワークスペース（主フォルダー）ごとに <c>%APPDATA%/Loomo/motion/&lt;鍵&gt;/</c>：
/// 見どころの定義 <c>probes.json</c>・録った時点ごとのフォルダー <c>snapshots/&lt;id&gt;/</c>（<c>snapshot.json</c> と絵）。
/// <b>リポジトリには何も書かない</b>。許した指紋はViewerと同じく置き場の外（<c>motion/approved.json</c>）——定義を書く Claude が
/// 自分で自分を許せないように。ディスクを読むので UI スレッドの打鍵の経路からは呼ばない。
/// <para>計画では SQLite（<c>motion.db</c>）だったが、絵を隣に置くのでフォルダーにした。古いものは数で刈る。</para>
/// </summary>
public sealed class MotionStore
{
    public const int KeepSnapshots = 200;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();

    public MotionStore(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo", "motion");
    }

    public string Root { get; }

    /// <summary>ワークスペースの鍵（主フォルダーのパスから。部屋の ID ではない——同じフォルダーなら同じ動き）。</summary>
    public static string KeyOf(string workspaceFolder)
    {
        var normalized = Path.GetFullPath(workspaceFolder).TrimEnd('\\', '/').ToLowerInvariant();
        var name = new string(Path.GetFileName(normalized).Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').Take(24).ToArray());
        return (name.Length > 0 ? name + "-" : "") + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..12].ToLowerInvariant();
    }

    public string FolderOf(string workspaceFolder) => Path.Combine(Root, KeyOf(workspaceFolder));

    public string ProbesPath(string workspaceFolder) => Path.Combine(FolderOf(workspaceFolder), "probes.json");

    private string SnapshotsFolder(string workspaceFolder) => Path.Combine(FolderOf(workspaceFolder), "snapshots");

    public string SnapshotFolder(string workspaceFolder, string snapshotId) => Path.Combine(SnapshotsFolder(workspaceFolder), snapshotId);

    /// <summary>見どころの定義。ファイルが無ければ空。読めなければ空と理由。</summary>
    public IReadOnlyList<MotionProbe> LoadProbes(string workspaceFolder, out string? error)
    {
        error = null;
        try
        {
            var path = ProbesPath(workspaceFolder);
            if (!File.Exists(path))
                return [];
            return MotionProbes.Parse(File.ReadAllText(path), out error) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return [];
        }
    }

    /// <summary>定義のひな形を置く（無いときだけ）。返すのはファイルの場所。</summary>
    public string EnsureProbesFile(string workspaceFolder)
    {
        var path = ProbesPath(workspaceFolder);
        if (File.Exists(path))
            return path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, MotionGuide.Template);
        return path;
    }

    // ── 許し ─────────────────────────────────────────────────

    private string ApprovedPath => Path.Combine(Root, "approved.json");

    public bool IsApproved(string workspaceFolder, IReadOnlyList<MotionProbe> probes)
    {
        lock (_gate)
            return ReadApproved()[KeyOf(workspaceFolder)] is JsonValue value && value.TryGetValue<string>(out var print)
                && print == MotionProbes.Fingerprint(probes);
    }

    public void Approve(string workspaceFolder, IReadOnlyList<MotionProbe> probes)
    {
        lock (_gate)
        {
            var approved = ReadApproved();
            approved[KeyOf(workspaceFolder)] = MotionProbes.Fingerprint(probes);
            try
            {
                Directory.CreateDirectory(Root);
                File.WriteAllText(ApprovedPath, approved.ToJsonString(Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private JsonObject ReadApproved()
    {
        try
        {
            if (File.Exists(ApprovedPath) && JsonNode.Parse(File.ReadAllText(ApprovedPath)) is JsonObject root)
                return root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new JsonObject();
    }

    // ── 録ったもの ───────────────────────────────────────────

    /// <summary>新しい順。</summary>
    public IReadOnlyList<MotionSnapshot> List(string workspaceFolder, int max = 60)
    {
        try
        {
            var folder = SnapshotsFolder(workspaceFolder);
            if (!Directory.Exists(folder))
                return [];
            return Directory.EnumerateDirectories(folder).Select(Path.GetFileName)
                .OrderByDescending(name => name, StringComparer.Ordinal).Take(max)
                .Select(name => Find(workspaceFolder, name!)).Where(snapshot => snapshot is not null).Select(s => s!).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public MotionSnapshot? Find(string workspaceFolder, string snapshotId)
    {
        if (!IsSnapshotId(snapshotId))
            return null;
        try
        {
            var path = Path.Combine(SnapshotFolder(workspaceFolder, snapshotId), "snapshot.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<MotionSnapshot>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>その時点より 1 つ前に録ったもの。言いつけの仕上がり（裏のワークツリー）で録ったものは「前」にしない。</summary>
    public MotionSnapshot? Previous(string workspaceFolder, MotionSnapshot snapshot)
        => List(workspaceFolder, KeepSnapshots).FirstOrDefault(other
            => string.CompareOrdinal(other.Id, snapshot.Id) < 0 && other.OrderResultId is null);

    /// <summary>いちばん新しい、作業ツリーで録ったもの。</summary>
    public MotionSnapshot? Latest(string workspaceFolder)
        => List(workspaceFolder, KeepSnapshots).FirstOrDefault(other => other.OrderResultId is null);

    public void Save(string workspaceFolder, MotionSnapshot snapshot)
    {
        var folder = SnapshotFolder(workspaceFolder, snapshot.Id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "snapshot.json"), JsonSerializer.Serialize(snapshot, Json));
        Prune(workspaceFolder);
    }

    /// <summary>絵のファイルの場所（スナップショットのフォルダーの中だけ）。無ければ null。</summary>
    public string? ImagePath(string workspaceFolder, string snapshotId, string fileName)
    {
        if (!IsSnapshotId(snapshotId) || fileName != Path.GetFileName(fileName))
            return null;
        var path = Path.Combine(SnapshotFolder(workspaceFolder, snapshotId), fileName);
        return File.Exists(path) ? path : null;
    }

    public string? ImagePrint(string workspaceFolder, MotionSnapshot snapshot, string fileName)
    {
        try
        {
            return ImagePath(workspaceFolder, snapshot.Id, fileName) is { } path
                ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IReadOnlyList<MotionChange> Compare(string workspaceFolder, MotionSnapshot snapshot, IReadOnlyList<MotionProbe> probes)
        => MotionDiff.Compare(Previous(workspaceFolder, snapshot), snapshot, probes,
            (owner, name) => ImagePrint(workspaceFolder, owner, name));

    private void Prune(string workspaceFolder)
    {
        try
        {
            foreach (var stale in Directory.EnumerateDirectories(SnapshotsFolder(workspaceFolder))
                         .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal).Skip(KeepSnapshots))
                Directory.Delete(stale, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static string NewSnapshotId(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyyMMdd-HHmmss-fff");

    private static bool IsSnapshotId(string id)
        => id.Length is > 0 and <= 40 && id.All(c => char.IsAsciiDigit(c) || c == '-');
}

/// <summary>録るときに添える、頼みごとの鍵（§43 と同じ）ときっかけ。</summary>
public sealed record MotionOrigin(string? SessionId, int PromptLine, string? PromptHead, string Trigger);

/// <summary>
/// 見どころを裏で回して、その時点の動きを録る（§44.3）。コマンドはViewerと同じ殻（<see cref="ViewerCommandRunner"/>）で、
/// 見えるターミナルには流さない。<b>回している間に作業ツリーが変わったら、その回は捨てる</b>（混ざった動きを録らない）。
/// </summary>
public sealed class MotionRecorder(MotionStore store)
{
    /// <summary>録れたらそのスナップショット。作業ツリーが途中で変わった・見どころが無い・印が <paramref name="skipIfStamp"/> と
    /// 同じ（前に録ってから何も変わっていない）なら null。</summary>
    /// <param name="runFolder">回す場所（既定はワークスペース）。言いつけの仕上がりは裏のワークツリーで回して、ワークスペースの置き場へ録る。</param>
    public async Task<MotionSnapshot?> RecordAsync(
        string workspaceFolder, IReadOnlyList<MotionProbe> probes, MotionOrigin origin, CancellationToken cancel,
        string? runFolder = null, string? skipIfStamp = null, string? orderResultId = null)
    {
        if (probes.Count == 0)
            return null;
        var cwd = runFolder ?? workspaceFolder;
        var stampBefore = await TreeStampAsync(cwd, cancel).ConfigureAwait(false);
        if (skipIfStamp is not null && stampBefore.Length > 0 && stampBefore == skipIfStamp)
            return null;
        var at = DateTimeOffset.Now;
        var id = MotionStore.NewSnapshotId(at);
        var folder = store.SnapshotFolder(workspaceFolder, id);
        var samples = new List<MotionSample>();
        try
        {
            foreach (var probe in probes)
            {
                cancel.ThrowIfCancellationRequested();
                samples.Add(await RunProbeAsync(cwd, probe, folder, cancel).ConfigureAwait(false));
            }
            var stampAfter = await TreeStampAsync(cwd, cancel).ConfigureAwait(false);
            if (stampBefore != stampAfter)
            {
                TryDelete(folder);
                return null;
            }
            var snapshot = new MotionSnapshot(id, at, origin.SessionId, origin.PromptLine, origin.PromptHead, origin.Trigger, samples)
            {
                Stamp = stampAfter,
                OrderResultId = orderResultId,
            };
            store.Save(workspaceFolder, snapshot);
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            TryDelete(folder);
            throw;
        }
    }

    private static async Task<MotionSample> RunProbeAsync(string workspaceFolder, MotionProbe probe, string snapshotFolder, CancellationToken cancel)
    {
        var outFolder = Path.Combine(Path.GetTempPath(), "loomo-motion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outFolder);
        var clock = Stopwatch.StartNew();
        try
        {
            var result = await ViewerCommandRunner.RunAsync(
                new ViewerCommand(probe.Id, probe.Command, ViewerCommand.CwdWorkspace, probe.TimeoutSeconds),
                workspaceFolder, [], null, _ => { }, cancel,
                new Dictionary<string, string> { ["LOOMO_OUT"] = outFolder }).ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();

            var tests = new List<MotionTest>();
            var images = new List<string>();
            if (probe.Kind == MotionProbeKind.Tests)
            {
                foreach (var file in Directory.EnumerateFiles(outFolder, "*", SearchOption.AllDirectories)
                             .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".trx" or ".xml")
                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    tests.AddRange(MotionTestResults.Parse(File.ReadAllText(file)));
            }
            else if (probe.Kind == MotionProbeKind.Image)
            {
                Directory.CreateDirectory(snapshotFolder);
                foreach (var file in Directory.EnumerateFiles(outFolder, "*.png", SearchOption.AllDirectories)
                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(24))
                {
                    var name = probe.Id + "." + Path.GetFileName(file);
                    File.Copy(file, Path.Combine(snapshotFolder, name), overwrite: true);
                    images.Add(name);
                }
            }
            var output = result.Output.Length > 200_000 ? result.Output[^200_000..] : result.Output;
            return new MotionSample(probe.Id, probe.Title, probe.Kind, result.ExitCode, result.TimedOut, output,
                tests.GroupBy(t => t.Name, StringComparer.Ordinal).Select(g => g.Last()).ToArray(), images, clock.ElapsedMilliseconds);
        }
        finally
        {
            TryDelete(outFolder);
        }
    }

    /// <summary>作業ツリーの印（HEAD と、変更のあるファイルの一覧）。git の外なら空——そのときは比べない。
    /// <c>--no-optional-locks</c>：これを外すと <c>.git</c> の見張りが自分の書き込みで鳴る（§40）。</summary>
    internal static async Task<string> TreeStampAsync(string workspaceFolder, CancellationToken cancel)
    {
        var head = await GitAsync(workspaceFolder, cancel, "rev-parse", "HEAD").ConfigureAwait(false);
        if (head is null)
            return "";
        var status = await GitAsync(workspaceFolder, cancel, "--no-optional-locks", "status", "--porcelain=v1").ConfigureAwait(false);
        return head + "\n" + status;
    }

    private static async Task<string?> GitAsync(string folder, CancellationToken cancel, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = folder,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var arg in args)
                info.ArgumentList.Add(arg);
            using var process = Process.Start(info);
            if (process is null)
                return null;
            var output = ChildProcessIo.ReadToEndAsync(process.StandardOutput, "動き:git:stdout");
            var error = ChildProcessIo.ReadToEndAsync(process.StandardError, "動き:git:stderr");
            await process.WaitForExitAsync(cancel).ConfigureAwait(false);
            await error.ConfigureAwait(false);
            return process.ExitCode == 0 ? await output.ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>見どころの書き方（§44.3）。MCP の <c>motion_guide</c> がモデルへ返す——だから英語。</summary>
public static class MotionGuide
{
    public const string Template = """
        {
          // 見どころ：Claude のターンが終わるたびに裏で回して、動き（出力・テストの成否・絵）を録る。
          // 行を変えたら、Loomo のパレット「動き：見どころを許す」でもう一度許す。
          "probes": [
            // { "id": "build", "title": "ビルド", "kind": "command", "command": "dotnet build -v q -nologo 2>&1 | Select-String -Pattern 'error|warn'", "timeoutSeconds": 300, "when": "turn", "ignore": ["経過時間"] },
            // { "id": "tests", "title": "テスト", "kind": "tests", "command": "dotnet test --logger \"trx;LogFileName=r.trx\" --results-directory $env:LOOMO_OUT", "timeoutSeconds": 900, "when": "manual" }
          ]
        }
        """;

    public static string Text(MotionStore store, string? workspaceFolder)
    {
        var path = workspaceFolder is null ? "(no workspace folder is open)" : store.ProbesPath(workspaceFolder);
        var existing = workspaceFolder is null ? [] : store.LoadProbes(workspaceFolder, out _);
        var approved = workspaceFolder is not null && existing.Count > 0 && store.IsApproved(workspaceFolder, existing);
        return $$"""
            # "Motion" in Loomo: reading changes as behaviour, not lines

            Loomo can record how the workspace behaves (command output, test outcomes, pictures) each time one of your turns
            ends, and show the user what changed between two points: "2 tests started failing", "this output gained a line".
            What is recorded is decided by probes, defined per workspace in one JSON file outside the repository:

              {{path}}

            You may write or edit this file when the user asks for a probe ("watch the build", "make the test suite a probe").
            The user must approve the command lines once in Loomo (palette: 動き：見どころを許す) before anything runs; changing a
            command line asks again. Tell the user to do that after you edit the file.

            ## probes.json
            { "probes": [
              { "id": "build", "title": "Build", "kind": "command",
                "command": "dotnet build -v q -nologo 2>&1 | Select-String -Pattern 'error|warn'",
                "timeoutSeconds": 300, "when": "turn", "ignore": ["Elapsed"] },
              { "id": "tests", "title": "Tests", "kind": "tests",
                "command": "dotnet test --logger \"trx;LogFileName=r.trx\" --results-directory $env:LOOMO_OUT",
                "timeoutSeconds": 900, "when": "manual" },
              { "id": "home", "title": "Home screen", "kind": "image",
                "command": "node tools/shot.js --out \"$env:LOOMO_OUT\\home.png\"", "when": "manual" }
            ] }
            - "command": a PowerShell command line, run hidden in the workspace's primary folder (never in the visible terminal).
            - "kind":
                command  the output text and exit code are compared
                tests    write TRX or JUnit XML files into the folder $env:LOOMO_OUT; pass/fail per test is compared
                image    write PNG files into $env:LOOMO_OUT; the pictures are compared
            - "when": "turn" (default; runs after every turn of yours, so keep it to seconds) or "manual" (runs only when the
              user asks, or from a standing order).
            - "ignore": regular expressions; matching output lines are dropped before comparing (timings, dates).
            - "timeoutSeconds": default 120, max 1800.
            The output must be deterministic for an unchanged tree, otherwise every turn looks like a change.

            ## Current state
            Probes defined: {{(existing.Count == 0 ? "(none)" : string.Join(", ", existing.Select(p => $"{p.Id} ({p.Kind.ToString().ToLowerInvariant()})")))}}
            Approved by the user: {{(approved ? "yes" : "no")}}
            """;
    }
}
