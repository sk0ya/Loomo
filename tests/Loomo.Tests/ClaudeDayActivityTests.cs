using System.IO;
using System.Linq;
using System.Text.Json;
using sk0ya.Loomo.Services;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// その日に Claude Code がしたこと（§43.6 C：軌跡の展開ビューの Claude の段）。記録からターンの区間と書き換えたファイルの数を引く。
/// ターンの区切りは頼みごとの印（§39.4 と同じ）・日付はローカル・拒否と失敗は書き換えに数えない。描画は対象外。
/// </summary>
public sealed class ClaudeDayActivityTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), $"loomo-day-activity-{Guid.NewGuid():N}");
    private readonly string _repo;
    private readonly string _projects;
    private readonly FakeWorkspaceService _workspace = new();

    public ClaudeDayActivityTests()
    {
        _repo = Path.Combine(_base, "repo");
        _projects = Path.Combine(_base, "projects");
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(_projects);
        _workspace.OpenFolder(_repo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* 後始末の失敗は無視 */ }
    }

    [Fact]
    public void その日のターンを_区間と書き換えたファイルの数つきで返す()
    {
        var day = DateOnly.FromDateTime(DateTime.Now);
        var t0 = day.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Local);
        var a = Path.Combine(_repo, "a.cs");
        var b = Path.Combine(_repo, "b.cs");
        WriteTranscript("s1",
            Prompt("直して", t0),
            ToolUse("t1", "Read", new { file_path = a }, t0.AddMinutes(1)), Result("t1", "ok", t0.AddMinutes(1)),
            ToolUse("t2", "Edit", new { file_path = a, old_string = "x", new_string = "y" }, t0.AddMinutes(2)), Result("t2", "ok", t0.AddMinutes(2)),
            ToolUse("t3", "Edit", new { file_path = a, old_string = "y", new_string = "z" }, t0.AddMinutes(3)), Result("t3", "ok", t0.AddMinutes(3)),
            ToolUse("t4", "Write", new { file_path = b, content = "b" }, t0.AddMinutes(5)), Result("t4", "ok", t0.AddMinutes(5)),
            // 拒否と失敗は書き換えていない。
            ToolUse("t5", "Write", new { file_path = Path.Combine(_repo, "c.cs"), content = "c" }, t0.AddMinutes(6)),
            Result("t5", "String to replace not found", t0.AddMinutes(6), isError: true),
            Prompt("これは何？", t0.AddMinutes(30)),
            ToolUse("t6", "Read", new { file_path = a }, t0.AddMinutes(31)), Result("t6", "ok", t0.AddMinutes(31)),
            Prompt("ありがとう", t0.AddMinutes(40)));

        var turns = new ClaudeDayActivity(_workspace, () => _projects).Read(day);

        Assert.Equal(["直して", "これは何？", "ありがとう"], turns.Select(turn => turn.Prompt));
        Assert.Equal(2, turns[0].FilesWritten);            // a.cs（2 回でも 1 つ）と b.cs
        Assert.Equal(5, turns[0].ToolCalls);
        Assert.Equal(t0, turns[0].Start.LocalDateTime);
        Assert.Equal(t0.AddMinutes(6), turns[0].End.LocalDateTime);
        Assert.Equal(0, turns[1].FilesWritten);
        Assert.Equal(t0.AddMinutes(31), turns[1].End.LocalDateTime);
        // 操作の無いターンは点（終わり＝始まり）。
        Assert.Equal(turns[2].Start, turns[2].End);
        Assert.Equal(0, turns[2].ToolCalls);
        Assert.Equal("s1", turns[0].SessionId);
    }

    [Fact]
    public void ほかの日のターンは返さない_記録が伸びたら読み直す()
    {
        var day = DateOnly.FromDateTime(DateTime.Now);
        var today = day.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Local);
        var transcript = WriteTranscript("s1",
            Prompt("昨日の頼み", today.AddDays(-1)),
            Prompt("今日の頼み", today));
        var activity = new ClaudeDayActivity(_workspace, () => _projects);

        Assert.Equal(["今日の頼み"], activity.Read(day).Select(turn => turn.Prompt));
        Assert.Equal(["昨日の頼み"], activity.Read(day.AddDays(-1)).Select(turn => turn.Prompt));

        File.AppendAllLines(transcript, [Prompt("もう一つ", today.AddHours(1))]);
        Assert.Equal(["今日の頼み", "もう一つ"], activity.Read(day).Select(turn => turn.Prompt));
    }

    private string WriteTranscript(string sessionId, params string[] lines)
    {
        var dir = Path.Combine(_projects, ClaudeTranscriptLocator.Slug(_repo));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, sessionId + ".jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string Stamp(DateTime local) => new DateTimeOffset(local).ToString("o");

    private string Prompt(string text, DateTime at)
        => JsonSerializer.Serialize(new { type = "user", timestamp = Stamp(at), cwd = _repo, message = new { role = "user", content = text } });

    private static string ToolUse(string id, string name, object input, DateTime at)
        => JsonSerializer.Serialize(new
        {
            type = "assistant",
            timestamp = Stamp(at),
            message = new { role = "assistant", content = new object[] { new { type = "tool_use", id, name, input } } },
        });

    private static string Result(string id, string text, DateTime at, bool isError = false)
        => JsonSerializer.Serialize(new
        {
            type = "user",
            timestamp = Stamp(at),
            message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = id, content = text, is_error = isError } } },
        });
}
