using System.IO;
using System.Linq;
using System.Text.Json;
using sk0ya.Loomo.Core.Harness;
using sk0ya.Loomo.Services;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 出自（§43）。コミットしていない行を、それを書いた Claude Code の頼みごとへ結ぶ。足した行の読み方（置換前にも
/// あった行は足した行ではない）・行の結び方（新しい書き込みが勝つ・どこにでもある行は隣に寄せる・人間の行は結ばない）・
/// 記録の読み方（拒否と失敗は書いていない・頼みごとはターンの区切りで決まる）・blame への載せ方を見る。画面は対象外。
/// </summary>
public sealed class ClaudeProvenanceTests : IAsyncLifetime
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), $"loomo-provenance-{Guid.NewGuid():N}");
    private string _repo = null!;
    private string _projects = null!;
    private FakeWorkspaceService _workspace = null!;
    private GitService _git = null!;

    public async Task InitializeAsync()
    {
        _repo = Path.Combine(_base, "repo");
        _projects = Path.Combine(_base, "projects");
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(_projects);
        _workspace = new FakeWorkspaceService();
        _workspace.OpenFolder(_repo);
        _git = new GitService(_workspace);
        await MustRunAsync("init");
        await MustRunAsync("symbolic-ref", "HEAD", "refs/heads/main");
        await MustRunAsync("config", "user.name", "Loomo Test");
        await MustRunAsync("config", "user.email", "loomo@example.invalid");
        await MustRunAsync("config", "core.autocrlf", "false");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* git の解放待ちは無視 */ }
        return Task.CompletedTask;
    }

    // ===== 足した行 =====

    [Fact]
    public void 置換前にもあった行は足した行に数えない()
    {
        var added = ClaudeProvenance.Added(
            "    void A() {\n        return;\n    }",
            "    void A() {\n        Log(\"a\");\n        return;\n    }");

        Assert.Equal(["        Log(\"a\");"], added);
    }

    [Fact]
    public void 同じ行を増やしたら_増えたぶんだけ足した行()
    {
        var added = ClaudeProvenance.Added("x();\n", "x();\nx();\n");

        Assert.Equal(["x();"], added.Where(line => line.Length > 0));
    }

    [Fact]
    public void 記録の行から_Edit_Write_MultiEdit_の足した行を読む()
    {
        Assert.Equal(["new line here"], ClaudeProvenance.ReadAddedLines(
            ToolUse("t1", "Edit", new { file_path = "a.cs", old_string = "keep", new_string = "keep\nnew line here" }), "t1"));
        Assert.Equal(["first", "second"], ClaudeProvenance.ReadAddedLines(
            ToolUse("t2", "Write", new { file_path = "a.cs", content = "first\r\nsecond" }), "t2"));
        Assert.Equal(["one added", "two added"], ClaudeProvenance.ReadAddedLines(
            ToolUse("t3", "MultiEdit", new
            {
                file_path = "a.cs",
                edits = new[]
                {
                    new { old_string = "a", new_string = "a\none added" },
                    new { old_string = "b", new_string = "two added" },
                },
            }), "t3"));
        // 別の id・書き込まないツールは読まない。
        Assert.Null(ClaudeProvenance.ReadAddedLines(ToolUse("t4", "Read", new { file_path = "a.cs" }), "t4"));
        Assert.Null(ClaudeProvenance.ReadAddedLines(ToolUse("t5", "Write", new { file_path = "a.cs", content = "x" }), "other"));
    }

    // ===== 行の結び方 =====

    [Fact]
    public void 同じ行を二度書いていたら_新しい書き込みが勝つ()
    {
        var old = Write("最初の頼み", 1, "var total = Sum(items);");
        var recent = Write("あとの頼み", 2, "var total = Sum(items);", "Console.WriteLine(total);");

        var map = ClaudeProvenance.Attribute(
            new Dictionary<int, string> { [4] = "var total = Sum(items);  ", [5] = "Console.WriteLine(total);" },
            [old, recent]);

        Assert.Same(recent, map[4]);
        Assert.Same(recent, map[5]);
    }

    [Fact]
    public void 複数の書き込みにある行は_続いている行の持ち主に寄せる()
    {
        // 「return result;」は両方にある。新しい方が勝つのではなく、その塊を書いた方へ。
        var old = Write("最初の頼み", 1, "var result = LoadEverything();", "try", "return result;");
        var recent = Write("あとの頼み", 2, "var other = ParseSomething();", "return result;");

        var map = ClaudeProvenance.Attribute(
            new Dictionary<int, string>
            {
                [10] = "var result = LoadEverything();", [11] = "try", [12] = "return result;",
                [30] = "var other = ParseSomething();", [31] = "return result;",
            },
            [old, recent]);

        Assert.All(new[] { 10, 11, 12 }, line => Assert.Same(old, map[line]));
        Assert.All(new[] { 30, 31 }, line => Assert.Same(recent, map[line]));
    }

    [Fact]
    public void 括弧や空行は中身で決めず_隣の書き込みに寄せる()
    {
        // 「}」はどの書き込みにもある。古い書き込みにも入っているが、続いている行を書いた新しい方へ寄せる。
        var old = Write("最初の頼み", 1, "void Old() {", "}");
        var recent = Write("あとの頼み", 2, "void Fresh() {", "    DoWork(now);", "}", "");

        var map = ClaudeProvenance.Attribute(
            new Dictionary<int, string> { [10] = "void Fresh() {", [11] = "    DoWork(now);", [12] = "}", [13] = "" },
            [old, recent]);

        Assert.All(new[] { 10, 11, 12, 13 }, line => Assert.Same(recent, map[line]));
    }

    [Fact]
    public void どの書き込みにも無い行は結ばない_人間の行を挟んだ括弧も()
    {
        var write = Write("頼み", 1, "void Fresh() {", "}");

        var map = ClaudeProvenance.Attribute(
            new Dictionary<int, string> { [0] = "void Fresh() {", [1] = "    HumanTyped(this);", [2] = "}", [7] = "}" },
            [write]);

        Assert.Same(write, map[0]);
        Assert.False(map.ContainsKey(1));
        // 2 行目の「}」は、上へ辿ると人間の行に当たる——Claude の行の続きとは言えない。
        Assert.False(map.ContainsKey(2));
        Assert.False(map.ContainsKey(7));
    }

    [Fact]
    public void 頼みごとの見出しは1行目だけ()
    {
        Assert.Equal("blame を直して", ClaudeProvenance.Head("  blame を直して\n詳しくは…"));
        Assert.Equal(80, ClaudeProvenance.Head(new string('あ', 200)).Length);
    }

    // ===== blame の読み方 =====

    [Fact]
    public void blame_からコミットしていない行だけを中身つきで読む()
    {
        var zero = new string('0', 40);
        var hash = new string('a', 40);
        var output = string.Join('\n',
            $"{hash} 1 1 1", "author Someone", "summary first", "\tcommitted line",
            $"{zero} 2 2 2", "author Not Committed Yet", "summary x", "\t    added by claude",
            $"{zero} 3 3", "\t",
            "");

        var lines = GitBlamePorcelainParser.ParseUncommitted(output);

        Assert.Equal(2, lines.Count);
        Assert.Equal("    added by claude", lines[1]);
        Assert.Equal("", lines[2]);
    }

    // ===== 記録の読み方（索引） =====

    [Fact]
    public void 索引は適用された書き込みだけを_頼みごとつきで返す()
    {
        var file = Path.Combine(_repo, "a.cs");
        WriteTranscript("s1",
            Prompt("ログを足して"),
            ToolUse("t1", "Edit", new { file_path = file, old_string = "x", new_string = "x\nLog.Write(\"added\");" }),
            Result("t1", "ok"),
            // 人間が拒否した書き込み・失敗した書き込みは、ファイルに入っていない。
            ToolUse("t2", "Edit", new { file_path = file, old_string = "x", new_string = "Rejected.Line();" }),
            Result("t2", "The user doesn't want to proceed with this tool use."),
            ToolUse("t3", "Edit", new { file_path = file, old_string = "x", new_string = "Failed.Line();" }),
            Result("t3", "String to replace not found", isError: true),
            Prompt("名前を変えて"),
            ToolUse("t4", "Edit", new { file_path = file.Replace('\\', '/'), old_string = "y", new_string = "Renamed.Call();" }),
            Result("t4", "ok"),
            ToolUse("t5", "Write", new { file_path = Path.Combine(_repo, "b.cs"), content = "class B { }" }),
            Result("t5", "ok"));
        var index = new ClaudeProvenanceIndex(_workspace, () => _projects);

        var writes = index.WritesFor(file);

        Assert.Equal(["ログを足して", "名前を変えて"], writes.Select(write => write.Prompt));
        Assert.Equal(["Log.Write(\"added\");"], writes[0].Lines);
        // 逆向き：2 つ目の頼みごとで変わった場所は a.cs と b.cs。
        Assert.Equal(["a.cs", "b.cs"], index.WritesOfRequest(writes[1]).Select(write => Path.GetFileName(write.FilePath)));
        // 取っ手は書き込みごとに 1 つで、引き直せる。
        var handle = index.HandleOf(writes[1]);
        Assert.Equal(handle, index.HandleOf(writes[1]));
        Assert.Same(writes[1], index.FromHandle(handle));
        Assert.Null(index.FromHandle(0));
    }

    [Fact]
    public void 記録が伸びたら読み直す()
    {
        var file = Path.Combine(_repo, "a.cs");
        var transcript = WriteTranscript("s1", Prompt("一つ目"),
            ToolUse("t1", "Write", new { file_path = file, content = "first content line" }), Result("t1", "ok"));
        var index = new ClaudeProvenanceIndex(_workspace, () => _projects);
        Assert.Single(index.WritesFor(file));

        File.AppendAllLines(transcript, [Prompt("二つ目"),
            ToolUse("t2", "Edit", new { file_path = file, old_string = "", new_string = "second content line" }), Result("t2", "ok")]);

        Assert.Equal(["一つ目", "二つ目"], index.WritesFor(file).Select(write => write.Prompt));
    }

    // ===== blame への載せ方（実 git） =====

    [Fact]
    public async Task コミットしていない行の_blame_に頼みごとが載る()
    {
        var file = Path.Combine(_repo, "a.cs");
        File.WriteAllText(file, "class A\n{\n}\n");
        await MustRunAsync("add", "-A");
        await MustRunAsync("commit", "-m", "first");
        // Claude が 1 行、人間が 1 行足した。
        File.WriteAllText(file, "class A\n{\n    void AddedByClaude() { }\n    void TypedByHuman() { }\n}\n");
        WriteTranscript("s1", Prompt("メソッドを足して\n（詳細）"),
            ToolUse("t1", "Edit", new { file_path = file, old_string = "{\n}", new_string = "{\n    void AddedByClaude() { }\n}" }),
            Result("t1", "ok"));
        var index = new ClaudeProvenanceIndex(_workspace, () => _projects);
        var service = new EditorGitService(_git, () => null, index);

        var blame = service.GetBlameLines(file);

        Assert.False(ClaudeBlame.IsClaude(blame[0]));   // コミット済みの行は git のまま
        Assert.True(ClaudeBlame.IsClaude(blame[2]));
        Assert.Equal("メソッドを足して", blame[2].Summary);
        Assert.Equal("メソッドを足して\n（詳細）", index.FromHandle(blame[2].OriginalLine)?.Prompt);
        Assert.False(blame.ContainsKey(3));             // 人間の行には何も出さない
        // 索引を渡さなければ、これまでどおりコミットしていない行は空。
        Assert.False(new EditorGitService(_git, () => null).GetBlameLines(file).ContainsKey(2));
    }

    [Fact]
    public async Task まだ_git_に載っていないファイルも_全行を結ぶ()
    {
        File.WriteAllText(Path.Combine(_repo, "seed.txt"), "seed\n");
        await MustRunAsync("add", "-A");
        await MustRunAsync("commit", "-m", "first");
        var file = Path.Combine(_repo, "fresh.cs");
        File.WriteAllText(file, "class Fresh\n{\n}\n");
        WriteTranscript("s1", Prompt("新しいクラスを作って"),
            ToolUse("t1", "Write", new { file_path = file, content = "class Fresh\n{\n}\n" }), Result("t1", "ok"));
        var service = new EditorGitService(_git, () => null, new ClaudeProvenanceIndex(_workspace, () => _projects));

        var blame = service.GetBlameLines(file);

        Assert.All(new[] { 0, 1, 2 }, line => Assert.True(ClaudeBlame.IsClaude(blame[line])));
    }

    // ===== コミットしても線を切らない（§43.6 A2a） =====

    [Fact]
    public void blame_からコミット済みの行を_中身とコミットの時刻つきで読む()
    {
        var zero = new string('0', 40);
        var hash = new string('a', 40);
        var output = string.Join('\n',
            $"{hash} 7 1 2", "author Someone", "author-time 1000", "committer-time 2000", "summary first", "\tcommitted line",
            $"{zero} 2 2 1", "author Not Committed Yet", "\tnot yet",
            $"{hash} 8 3", "\tsecond committed line",
            "");

        var lines = GitBlamePorcelainParser.ParseCommitted(output);

        Assert.Equal(2, lines.Count);
        Assert.Equal(new GitBlameSourceLine(0, "committed line", "aaaaaaa", 7, DateTimeOffset.FromUnixTimeSeconds(2000)), lines[0]);
        // 同じコミットの 2 つ目以降は見出しだけ（時刻は最初の 1 回から引く）。
        Assert.Equal(new GitBlameSourceLine(2, "second committed line", "aaaaaaa", 8, DateTimeOffset.FromUnixTimeSeconds(2000)), lines[1]);
    }

    [Fact]
    public async Task コミットした行にも頼みごとが添えられ_コミットの情報は残る()
    {
        var file = Path.Combine(_repo, "a.cs");
        File.WriteAllText(file, "class A\n{\n}\n");
        await MustRunAsync("add", "-A");
        await MustRunAsync("commit", "-m", "first");
        File.WriteAllText(file, "class A\n{\n    void AddedByClaude() { }\n    void TypedByHuman() { }\n}\n");
        WriteTranscript("s1", Prompt("メソッドを足して\n（詳細）"),
            ToolUse("t1", "Edit", new { file_path = file, old_string = "{\n}", new_string = "{\n    void AddedByClaude() { }\n}" }),
            Result("t1", "ok"));
        await MustRunAsync("add", "-A");
        await MustRunAsync("commit", "-m", "second");
        var index = new ClaudeProvenanceIndex(_workspace, () => _projects);
        var service = new EditorGitService(_git, () => null, index);

        var blame = service.GetBlameLines(file);

        // git の行のまま（ハッシュ・元の行番号＝差分と履歴の経路）で、件名の前に頼みごとが付く。
        var line = blame[2];
        Assert.False(ClaudeBlame.IsClaude(line));
        Assert.True(line.CommitHash.All(char.IsAsciiHexDigit));
        Assert.Equal(3, line.OriginalLine);
        Assert.Equal("頼みごと「メソッドを足して」 · second", line.Summary);
        Assert.Equal("メソッドを足して\n（詳細）", index.ForCommitLine(file, line.CommitHash, line.OriginalLine)?.Prompt);
        // 同じコミットの人間の行・前のコミットの行には付かない。
        Assert.Equal("second", blame[3].Summary);
        Assert.Null(index.ForCommitLine(file, blame[3].CommitHash, blame[3].OriginalLine));
        Assert.Equal("first", blame[0].Summary);
        // 索引を渡さなければ git のまま。
        Assert.Equal("second", new EditorGitService(_git, () => null).GetBlameLines(file)[2].Summary);
    }

    [Fact]
    public async Task コミットより後の書き込みは_その行を書いていない()
    {
        var file = Path.Combine(_repo, "a.cs");
        File.WriteAllText(file, "class A\n{\n    void WrittenBefore() { }\n}\n");
        await MustRunAsync("add", "-A");
        await MustRunAsync("commit", "-m", "first");
        // コミットの後で、同じ中身を含むファイルを丸ごと書き直した（Write は全行が「足した行」になる）。
        var later = DateTimeOffset.UtcNow.AddHours(1);
        WriteTranscript("s1", Prompt("書き直して", later),
            ToolUse("t1", "Write", new { file_path = file, content = "class A\n{\n    void WrittenBefore() { }\n}\n" }, later.AddSeconds(1)),
            Result("t1", "ok", at: later.AddSeconds(2)));
        var index = new ClaudeProvenanceIndex(_workspace, () => _projects);

        var blame = new EditorGitService(_git, () => null, index).GetBlameLines(file);

        Assert.Equal("first", blame[2].Summary);
        Assert.Null(index.ForCommitLine(file, blame[2].CommitHash, blame[2].OriginalLine));
    }

    // ===== 道具 =====

    private static int _clock;

    private static ClaudeWrite Write(string prompt, int minute, params string[] lines)
        => new("s", "t.jsonl", "a.cs", prompt, minute, new DateTimeOffset(2026, 11, 14, 10, minute, 0, TimeSpan.Zero), lines, false);

    private string WriteTranscript(string sessionId, params string[] lines)
    {
        var dir = Path.Combine(_projects, ClaudeTranscriptLocator.Slug(_repo));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, sessionId + ".jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string Stamp() => new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero).AddSeconds(++_clock).ToString("o");

    private string Prompt(string text, DateTimeOffset? at = null)
        => JsonSerializer.Serialize(new { type = "user", timestamp = at?.ToString("o") ?? Stamp(), cwd = _repo, message = new { role = "user", content = text } });

    private static string ToolUse(string id, string name, object input, DateTimeOffset? at = null)
        => JsonSerializer.Serialize(new
        {
            type = "assistant",
            timestamp = at?.ToString("o") ?? Stamp(),
            message = new { role = "assistant", content = new object[] { new { type = "tool_use", id, name, input } } },
        });

    private static string Result(string id, string text, bool isError = false, DateTimeOffset? at = null)
        => JsonSerializer.Serialize(new
        {
            type = "user",
            timestamp = at?.ToString("o") ?? Stamp(),
            message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = id, content = text, is_error = isError } } },
        });

    private async Task MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, $"git {string.Join(' ', args)}: {result.Message}");
    }
}
