using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Orders;
using sk0ya.Loomo.Core.Processes;
using sk0ya.Loomo.Services.Motion;

namespace sk0ya.Loomo.Services.Orders;

/// <summary>言いつけを断る・進められない理由（人間向けの日本語。Viewerの Promise の失敗としてそのまま出る）。</summary>
public sealed class OrderException(string message) : Exception(message);

/// <summary>
/// 言いつけ（§44.4）。常設の頼みごとを、<b>人間の作業ツリーに触れずに</b>裏のワークツリーで Claude Code に進めさせ、
/// 仕上がりをブランチ <c>loomo/order/&lt;名前&gt;/&lt;日時&gt;</c> のコミットとして盆に並べる。採る＝そのコミットの変更を
/// いまの作業ツリーへ当てる（コミットはしない・先に衝突を予告する）。捨てる＝ワークツリーとブランチを消す。push はしない。
/// <para>置き場はワークスペース（主フォルダー）ごとに <c>%APPDATA%/Loomo/orders/&lt;鍵&gt;/</c>（<c>orders.json</c>・<c>results.json</c>）、
/// ワークツリーは <c>%LOCALAPPDATA%/Loomo/orders/&lt;鍵&gt;/</c>。リポジトリの中には何も置かない（ブランチだけは git の中に増える）。</para>
/// <para>走るのは同時に 1 本。Loomo が動いている間だけ。</para>
/// </summary>
public sealed class OrderService
{
    public const int KeepResults = 50;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _root;
    private readonly string _worktreeRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _fileGate = new();

    public OrderService(string? root = null, string? worktreeRoot = null)
    {
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo", "orders");
        _worktreeRoot = worktreeRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Loomo", "orders");
    }

    /// <summary>いま進めている言いつけの名前（無ければ null）。</summary>
    public string? Running { get; private set; }

    /// <summary>言いつけ・盆が変わった（背景スレッドで鳴ることがある）。</summary>
    public event Action? Changed;

    /// <summary>裏の Claude Code を起こす実体。テストで差し替える。引数は（作業フォルダー, 頼みごと, 許す道具, 期限）。</summary>
    public Func<string, string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<(int ExitCode, string Output)>> RunAgent { get; set; }
        = RunClaudeAsync;

    private string FolderOf(string workspaceFolder) => Path.Combine(_root, MotionStore.KeyOf(workspaceFolder));

    // ── 言いつけ ─────────────────────────────────────────────

    public IReadOnlyList<StandingOrder> Orders(string workspaceFolder)
        => Read<List<StandingOrder>>(Path.Combine(FolderOf(workspaceFolder), "orders.json")) ?? [];

    public void Save(string workspaceFolder, StandingOrder order)
    {
        if (StandingOrder.Validate(order.Id, order.Prompt, order.Trigger, order.At) is { } problem)
            throw new OrderException(problem);
        lock (_fileGate)
        {
            var orders = Orders(workspaceFolder).Where(o => o.Id != order.Id).Append(order).OrderBy(o => o.Id, StringComparer.Ordinal).ToList();
            Write(Path.Combine(FolderOf(workspaceFolder), "orders.json"), orders);
        }
        Changed?.Invoke();
    }

    public void Remove(string workspaceFolder, string orderId)
    {
        lock (_fileGate)
            Write(Path.Combine(FolderOf(workspaceFolder), "orders.json"), Orders(workspaceFolder).Where(o => o.Id != orderId).ToList());
        Changed?.Invoke();
    }

    // ── 盆 ───────────────────────────────────────────────────

    /// <summary>新しい順。</summary>
    public IReadOnlyList<OrderResult> Results(string workspaceFolder)
        => (Read<List<OrderResult>>(Path.Combine(FolderOf(workspaceFolder), "results.json")) ?? [])
            .OrderByDescending(r => r.At).ToArray();

    private void PutResult(string workspaceFolder, OrderResult result)
    {
        lock (_fileGate)
        {
            var results = Results(workspaceFolder).Where(r => r.Id != result.Id).Prepend(result).Take(KeepResults).ToList();
            Write(Path.Combine(FolderOf(workspaceFolder), "results.json"), results);
        }
        Changed?.Invoke();
    }

    private void DropResult(string workspaceFolder, string resultId)
    {
        lock (_fileGate)
            Write(Path.Combine(FolderOf(workspaceFolder), "results.json"), Results(workspaceFolder).Where(r => r.Id != resultId).ToList());
        Changed?.Invoke();
    }

    /// <summary>今日その言いつけを走らせた回数と、最後に走らせた時刻（盆から捨てたものは数えない）。</summary>
    public (int Today, DateTime? Last) Runs(string workspaceFolder, string orderId, DateTime now)
    {
        var mine = Results(workspaceFolder).Where(r => r.OrderId == orderId).ToArray();
        return (mine.Count(r => r.At.LocalDateTime.Date == now.Date), mine.Length == 0 ? null : mine.Max(r => r.At.LocalDateTime));
    }

    // ── 走らせる ─────────────────────────────────────────────

    /// <summary>言いつけを 1 つ進める。<paramref name="note"/> は頼みごとの後ろに添える部屋からの一言（動きの変化など）。</summary>
    /// <param name="recordMotion">仕上がりのワークツリーで動きを録って、1 行で返す（無ければ null）。</param>
    public async Task<OrderResult> RunAsync(string workspaceFolder, string orderId, string? note,
        Func<string, string, CancellationToken, Task<string?>>? recordMotion, CancellationToken cancel)
    {
        var order = Orders(workspaceFolder).FirstOrDefault(o => o.Id == orderId)
            ?? throw new OrderException($"言いつけ「{orderId}」はありません");
        if (!await _gate.WaitAsync(0, cancel).ConfigureAwait(false))
            throw new OrderException($"「{Running}」を進めている最中です（同時に 1 本だけ）");
        var at = DateTimeOffset.Now;
        var resultId = at.ToUniversalTime().ToString("yyyyMMdd-HHmmss");
        var result = new OrderResult(resultId, orderId, at, OrderResult.Running, null, null, null, null, 0, "", null);
        try
        {
            Running = orderId;
            PutResult(workspaceFolder, result);

            var repo = (await GitAsync(workspaceFolder, cancel, "rev-parse", "--show-toplevel").ConfigureAwait(false)).Output.Trim();
            var head = await GitAsync(workspaceFolder, cancel, "rev-parse", "HEAD").ConfigureAwait(false);
            if (repo.Length == 0 || head.ExitCode != 0)
                throw new OrderException("ワークスペースが git のリポジトリではありません（言いつけは裏のワークツリーで進めます）");
            var baseCommit = head.Output.Trim();
            var branch = $"loomo/order/{orderId}/{resultId}";
            var worktree = Path.Combine(_worktreeRoot, MotionStore.KeyOf(workspaceFolder), $"{orderId}-{resultId}");
            Directory.CreateDirectory(Path.GetDirectoryName(worktree)!);
            var added = await GitAsync(repo, cancel, "worktree", "add", "-b", branch, worktree, baseCommit).ConfigureAwait(false);
            if (added.ExitCode != 0)
                throw new OrderException("裏のワークツリーを作れませんでした: " + added.Output.Trim());
            result = result with { Branch = branch, Worktree = worktree, BaseCommit = baseCommit };
            PutResult(workspaceFolder, result);

            var prompt = order.Prompt + (string.IsNullOrWhiteSpace(note) ? "" : "\n\n---\n" + note);
            prompt += "\n\n（Loomo の言いつけとして、人間のいない裏のワークツリーで動いています。終わったら、何をしたかを短くまとめてください。"
                      + "コミット・push・ブランチの操作はしないでください。変更は Loomo がまとめてコミットします。）";
            var (exit, output) = await RunAgent(worktree, prompt, order.Tools, TimeSpan.FromMinutes(order.TimeoutMinutes), cancel).ConfigureAwait(false);
            var summary = output.Trim();
            if (summary.Length > 4000)
                summary = summary[..4000] + "…";

            await GitAsync(worktree, cancel, "add", "-A").ConfigureAwait(false);
            var commit = await GitAsync(worktree, cancel, "-c", "user.name=Loomo", "-c", "user.email=loomo@localhost",
                "commit", "-q", "--no-verify", "-m", $"言いつけ「{orderId}」の仕上がり").ConfigureAwait(false);
            var tip = (await GitAsync(worktree, cancel, "rev-parse", "HEAD").ConfigureAwait(false)).Output.Trim();
            var files = commit.ExitCode == 0 && tip != baseCommit
                ? (await GitAsync(worktree, cancel, "diff", "--name-only", baseCommit, tip).ConfigureAwait(false)).Output
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
                : 0;
            if (files == 0)
            {
                await CleanupAsync(repo, worktree, branch).ConfigureAwait(false);
                result = result with
                {
                    Status = exit == 0 ? OrderResult.Empty : OrderResult.Failed, Branch = null, Worktree = null,
                    Summary = summary.Length > 0 ? summary : exit == 0 ? "（変更はありませんでした）" : $"（終了コード {exit}）",
                };
            }
            else
            {
                string? motion = null;
                if (recordMotion is not null)
                {
                    try { motion = await recordMotion(worktree, resultId, cancel).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { motion = "動きを録れませんでした: " + ex.Message; }
                }
                result = result with
                {
                    Status = OrderResult.Done, Commit = tip, Files = files, Motion = motion,
                    Summary = exit == 0 ? summary : $"（終了コード {exit}・途中までの変更）\n" + summary,
                };
            }
            PutResult(workspaceFolder, result);
            return result;
        }
        catch (Exception ex) when (ex is OrderException or IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            if (result.Worktree is { } worktree && result.Branch is { } branch)
            {
                var repo = (await GitAsync(workspaceFolder, CancellationToken.None, "rev-parse", "--show-toplevel").ConfigureAwait(false)).Output.Trim();
                await CleanupAsync(repo, worktree, branch).ConfigureAwait(false);
            }
            result = result with { Status = OrderResult.Failed, Branch = null, Worktree = null, Summary = ex.Message };
            PutResult(workspaceFolder, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            DropResult(workspaceFolder, resultId);
            throw;
        }
        finally
        {
            Running = null;
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>採る：仕上がりのコミットの変更を、いまの作業ツリーへ当てる（コミットはしない）。先に衝突を予告し、
    /// ぶつかるなら何もせずにファイルを挙げて断る。</summary>
    public async Task<OrderResult> TakeAsync(string workspaceFolder, string resultId, CancellationToken cancel)
    {
        var result = Results(workspaceFolder).FirstOrDefault(r => r.Id == resultId)
            ?? throw new OrderException("その仕上がりはもうありません");
        if (result.Status != OrderResult.Done || result.Commit is not { } commit || result.BaseCommit is not { } baseCommit)
            throw new OrderException("採れるのは、変更のある仕上がりだけです");
        var repo = (await GitAsync(workspaceFolder, cancel, "rev-parse", "--show-toplevel").ConfigureAwait(false)).Output.Trim();
        var forecast = await GitAsync(repo, cancel, "merge-tree", baseCommit, "HEAD", commit).ConfigureAwait(false);
        var conflicts = GitMergeTreeParser.ParseLegacyConflicts(forecast.Output);
        if (conflicts.Count > 0)
            throw new OrderException("いまのブランチとぶつかります（何も変えていません）: " + string.Join("、", conflicts.Take(8))
                                     + (conflicts.Count > 8 ? $" ほか {conflicts.Count - 8} 件" : ""));
        var picked = await GitAsync(repo, cancel, "cherry-pick", "--no-commit", commit).ConfigureAwait(false);
        if (picked.ExitCode != 0)
            throw new OrderException("当てられませんでした（作業ツリーの未保存・未コミットの変更とぶつかっていないか確かめてください）: "
                                     + picked.Output.Trim());
        await CleanupAsync(repo, result.Worktree, result.Branch).ConfigureAwait(false);
        var taken = result with { Status = OrderResult.Taken, Worktree = null, Branch = null };
        PutResult(workspaceFolder, taken);
        return taken;
    }

    /// <summary>捨てる：ワークツリーとブランチを消し、盆から外す。</summary>
    public async Task DiscardAsync(string workspaceFolder, string resultId)
    {
        if (Results(workspaceFolder).FirstOrDefault(r => r.Id == resultId) is not { } result)
            return;
        if (result.Status == OrderResult.Running)
            throw new OrderException("進めている最中の言いつけは捨てられません");
        var repo = (await GitAsync(workspaceFolder, CancellationToken.None, "rev-parse", "--show-toplevel").ConfigureAwait(false)).Output.Trim();
        await CleanupAsync(repo, result.Worktree, result.Branch).ConfigureAwait(false);
        DropResult(workspaceFolder, resultId);
    }

    /// <summary>起動したとき：前の Loomo が進めている途中で終わった言いつけを「失敗」にする。</summary>
    public void RecoverInterrupted(string workspaceFolder)
    {
        foreach (var stale in Results(workspaceFolder).Where(r => r.Status == OrderResult.Running))
            PutResult(workspaceFolder, stale with { Status = OrderResult.Failed, Summary = "Loomo が途中で終わりました" });
    }

    private static async Task CleanupAsync(string repo, string? worktree, string? branch)
    {
        if (repo.Length == 0)
            return;
        if (worktree is not null)
        {
            await GitAsync(repo, CancellationToken.None, "worktree", "remove", "--force", worktree).ConfigureAwait(false);
            try { if (Directory.Exists(worktree)) Directory.Delete(worktree, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            await GitAsync(repo, CancellationToken.None, "worktree", "prune").ConfigureAwait(false);
        }
        if (branch is not null)
            await GitAsync(repo, CancellationToken.None, "branch", "-D", branch).ConfigureAwait(false);
    }

    // ── 子プロセス ───────────────────────────────────────────

    internal static async Task<(int ExitCode, string Output)> GitAsync(string folder, CancellationToken cancel, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException("git を起動できません");
            var output = ChildProcessIo.ReadToEndAsync(process.StandardOutput, "言いつけ:git:stdout");
            var error = ChildProcessIo.ReadToEndAsync(process.StandardError, "言いつけ:git:stderr");
            await process.WaitForExitAsync(cancel).ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>裏の Claude Code を対話なしで起こす。頼みごとは標準入力で渡す（引用符の取り違えを避ける）。
    /// 許す道具は <paramref name="tools"/> だけで、push は道具の側でも断る。Loomo の端末から継いだ印（MCP の橋が部屋へ
    /// つなぐための <c>LOOMO_TERMINAL_TAB</c> など）は外す——裏の Claude が人間の画面に物を置かないように。</summary>
    public static async Task<(int ExitCode, string Output)> RunClaudeAsync(
        string folder, string prompt, IReadOnlyList<string> tools, TimeSpan timeout, CancellationToken cancel)
    {
        var info = new ProcessStartInfo(ResolveClaude())
        {
            WorkingDirectory = folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in new[] { "-p", "--output-format", "text", "--permission-mode", "acceptEdits" })
            info.ArgumentList.Add(arg);
        info.ArgumentList.Add("--allowedTools");
        foreach (var tool in tools)
            info.ArgumentList.Add(tool);
        info.ArgumentList.Add("--disallowedTools");
        info.ArgumentList.Add("Bash(git push:*)");
        foreach (var name in new[] { "LOOMO_TERMINAL_TAB", "CLAUDE_CODE_SSE_PORT", "ENABLE_IDE_INTEGRATION" })
            info.Environment.Remove(name);

        using var process = Process.Start(info) ?? throw new InvalidOperationException("claude を起動できません");
        ChildProcessIo.TrySetBackgroundPriority(process);
        var output = ChildProcessIo.ReadToEndAsync(process.StandardOutput, "言いつけ:claude:stdout");
        var error = ChildProcessIo.ReadToEndAsync(process.StandardError, "言いつけ:claude:stderr");
        try
        {
            await process.StandardInput.WriteAsync(prompt).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (IOException) { }
        using var limit = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, limit.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (cancel.IsCancellationRequested)
                throw;
            return (-1, $"（{timeout.TotalMinutes:0} 分で打ち切りました）");
        }
        return (process.ExitCode, (await output.ConfigureAwait(false)).Trim() is { Length: > 0 } text ? text : await error.ConfigureAwait(false));
    }

    private static string ResolveClaude()
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
            {
                try
                {
                    var path = Path.Combine(folder.Trim('"'), name);
                    if (File.Exists(path))
                        return path;
                }
                catch (ArgumentException) { }
            }
        }
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        return File.Exists(local) ? local : "claude";
    }

    // ── ファイル ─────────────────────────────────────────────

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, overwrite: true);
    }
}
