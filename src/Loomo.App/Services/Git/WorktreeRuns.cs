using System.IO;
using System.Text;
using System.Text.Json;

namespace sk0ya.Loomo.App.Services;

/// <summary>ワークツリーで回したコマンド 1 回（§24.104）。終わるまで <see cref="ExitCode"/> は null。</summary>
public sealed record WorktreeRun(string WorktreePath, string Command, DateTime StartedUtc, int? ExitCode = null, DateTime? FinishedUtc = null)
{
    public bool IsRunning => FinishedUtc is null;
}

/// <summary>
/// ワークツリーの行から決まったコマンドを回す（§24.104・GitLens 19.2.0 の Run Default Task）の、純粋な部分。
/// 行に出す印と、リポジトリごとに覚えるコマンドの置き場。
/// </summary>
public static class WorktreeRuns
{
    /// <summary>行に出すコマンドの長さ（超えたら … で切る）。行は狭いので、何を回したか分かる分だけ。</summary>
    public const int MarkCommandLength = 24;

    /// <summary>行の右に出す印：回している間は「▶ dotnet test」、通れば「✓ dotnet test」、落ちれば「✗ dotnet test」。</summary>
    public static string Mark(WorktreeRun run)
    {
        var command = run.Command.Length > MarkCommandLength ? run.Command[..(MarkCommandLength - 1)] + "…" : run.Command;
        return run switch
        {
            { IsRunning: true } => $"▶ {command}",
            { ExitCode: 0 } => $"✓ {command}",
            _ => $"✗ {command}",
        };
    }

    /// <summary>印に添える説明（ツールチップ）。</summary>
    public static string Describe(WorktreeRun run, DateTime nowUtc)
    {
        var when = run.FinishedUtc ?? run.StartedUtc;
        var ago = Ago(nowUtc - when);
        return run switch
        {
            { IsRunning: true } => $"{run.Command}\n実行中（{ago}に開始）",
            { ExitCode: 0 } => $"{run.Command}\n成功（{ago}）",
            { ExitCode: { } code } => $"{run.Command}\n失敗・終了コード {code}（{ago}）",
            _ => $"{run.Command}\n終了コード不明（{ago}）",
        };
    }

    /// <summary>ワークツリーのパス → 印。同じワークツリーで何度も回したら最後のものだけ。</summary>
    public static IReadOnlyDictionary<string, string> Marks(IEnumerable<WorktreeRun> runs)
    {
        var marks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in runs.OrderBy(r => r.StartedUtc))
            marks[Path.TrimEndingDirectorySeparator(run.WorktreePath)] = Mark(run);
        return marks;
    }

    private static string Ago(TimeSpan age)
        => age.TotalMinutes < 1 ? "たった今"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} 分前"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} 時間前"
            : $"{(int)age.TotalDays} 日前";

    /// <summary>リポジトリ（メインのワークツリー）ごとに覚えるコマンドの置き場（<c>%APPDATA%\Loomo\worktree-run-commands.json</c>）。</summary>
    public static string DefaultStorePath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo", "worktree-run-commands.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>そのリポジトリで前に回したコマンド。無ければ null。読めないファイルは無いものとして扱う。</summary>
    public static string? RememberedCommand(string storePath, string repository)
        => Load(storePath).TryGetValue(Key(repository), out var command) && !string.IsNullOrWhiteSpace(command) ? command : null;

    /// <summary>そのリポジトリのコマンドを覚える（ワークツリーが変わっても同じリポジトリなら同じコマンドを出す）。</summary>
    public static void Remember(string storePath, string repository, string command)
    {
        var all = Load(storePath);
        all[Key(repository)] = command.Trim();
        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        File.WriteAllText(storePath, JsonSerializer.Serialize(all, Json), new UTF8Encoding(false));
    }

    private static Dictionary<string, string> Load(string storePath)
    {
        try
        {
            if (File.Exists(storePath)
                && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(storePath)) is { } loaded)
                return new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static string Key(string repository) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository)).ToLowerInvariant();
}
