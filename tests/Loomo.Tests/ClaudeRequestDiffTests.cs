using System.IO;
using System.Linq;
using System.Text.Json;
using sk0ya.Loomo.Core.Harness;
using sk0ya.Loomo.Services;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 頼みごと単位の差分（§43.6 A3）。純粋な部分（操作の読み方・当て直し・抜粋）と、記録＋ファイル履歴から前後を組む
/// ところ（前の中身の探し方：ファイル履歴 → 結果の行 → 抜粋、いまのファイルとの比べ方）、Diff ペインの素材への
/// 直し方（注記・印・「見た」印の見方）を見る。git は使わない。画面は対象外。
/// </summary>
public sealed class ClaudeRequestDiffTests : IDisposable
{
    private const string Original = "class A\n{\n    void One() { }\n    void Two() { }\n}\n";
    private const string AfterTwoEdits = "class A\n{\n    void One() { }\n    void AddedFirst() { }\n    void TwoRenamed() { }\n}\n";

    private readonly string _base = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"loomo-request-diff-{Guid.NewGuid():N}"));
    private readonly string _repo;
    private readonly string _projects;
    private readonly string _history;
    private readonly FakeWorkspaceService _workspace;
    private int _clock;

    public ClaudeRequestDiffTests()
    {
        _repo = Path.Combine(_base, "repo");
        _projects = Path.Combine(_base, "projects");
        _history = Path.Combine(_base, "file-history");
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(_projects);
        Directory.CreateDirectory(_history);
        _workspace = new FakeWorkspaceService();
        _workspace.OpenFolder(_repo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* 後始末の失敗は無視 */ }
    }

    // ===== 当て直し（Replay） =====

    [Fact]
    public void Edit_は最初の1か所だけを置き換える()
    {
        Assert.Equal("X b a", ClaudeRequestDiff.Replay("a b a", [new ClaudeEditOp("a", "X", false, false)]));
    }

    [Fact]
    public void replace_all_の_Edit_は全部を置き換える()
    {
        Assert.Equal("X b X", ClaudeRequestDiff.Replay("a b a", [new ClaudeEditOp("a", "X", true, false)]));
    }

    [Fact]
    public void Write_はファイル全体を置き換え_Edit_Write_Edit_の順でも当たる()
    {
        var after = ClaudeRequestDiff.Replay("old text\n",
        [
            new ClaudeEditOp("old", "older", false, false),
            new ClaudeEditOp(null, "new\ncontent\n", false, true),
            new ClaudeEditOp("content", "body", false, false),
        ]);

        Assert.Equal("new\nbody\n", after);
        // Write だけなら、前の中身が何であっても後は書いた中身。
        Assert.Equal("whole", ClaudeRequestDiff.Replay("anything", [new ClaudeEditOp(null, "whole", false, true)]));
    }

    [Fact]
    public void 置換前が見つからなければ_null_半端な後は作らない()
    {
        Assert.Null(ClaudeRequestDiff.Replay("alpha beta", [new ClaudeEditOp("gamma", "delta", false, false)]));
        // 1 つ目は当たるが 2 つ目が当たらない——1 つ目だけ当てた中身は返さない。
        Assert.Null(ClaudeRequestDiff.Replay("alpha beta",
        [
            new ClaudeEditOp("alpha", "ALPHA", false, false),
            new ClaudeEditOp("gamma", "delta", false, false),
        ]));
        Assert.Null(ClaudeRequestDiff.Replay("alpha beta", [new ClaudeEditOp("gamma", "delta", true, false)]));
    }

    [Fact]
    public void 置換前が空の_Edit_は空のファイルには当たり_中身のあるファイルには_null()
    {
        Assert.Equal("created\n", ClaudeRequestDiff.Replay("", [new ClaudeEditOp("", "created\n", false, false)]));
        Assert.Equal("created\n", ClaudeRequestDiff.Replay("", [new ClaudeEditOp(null, "created\n", false, false)]));
        Assert.Null(ClaudeRequestDiff.Replay("existing\n", [new ClaudeEditOp("", "created\n", false, false)]));
    }

    [Fact]
    public void CRLF_の前の中身でも当たり_結果は_LF()
    {
        var after = ClaudeRequestDiff.Replay("first\r\nsecond\r\nthird\r\n", [new ClaudeEditOp("first\nsecond", "first\nchanged", false, false)]);

        Assert.Equal("first\nchanged\nthird\n", after);
    }

    [Fact]
    public void 操作が無ければ_前の中身を_LF_にそろえて返す()
    {
        Assert.Equal("a\nb\n", ClaudeRequestDiff.Replay("a\r\nb\r\n", []));
    }

    [Fact]
    public void Normalize_は_CRLF_を_LF_にし_null_は空にする()
    {
        Assert.Equal("a\nb\n", ClaudeRequestDiff.Normalize("a\r\nb\n"));
        Assert.Equal("", ClaudeRequestDiff.Normalize(null));
        Assert.Equal("", ClaudeRequestDiff.Normalize(""));
    }

    // ===== 操作の読み方（ReadOperations） =====

    [Fact]
    public void 記録の行から_Edit_を読む_改行は_LF_にそろう()
    {
        var ops = ClaudeRequestDiff.ReadOperations(
            ToolUse("t1", "Edit", new { file_path = "a.cs", old_string = "keep\r\nold", new_string = "keep\r\nnew" }), "t1");

        Assert.Equal(new ClaudeEditOp("keep\nold", "keep\nnew", false, false), Assert.Single(ops!));
    }

    [Fact]
    public void 記録の行から_replace_all_を読む()
    {
        var all = ClaudeRequestDiff.ReadOperations(
            ToolUse("t1", "Edit", new { file_path = "a.cs", old_string = "x", new_string = "y", replace_all = true }), "t1");
        var one = ClaudeRequestDiff.ReadOperations(
            ToolUse("t2", "Edit", new { file_path = "a.cs", old_string = "x", new_string = "y", replace_all = false }), "t2");

        Assert.True(Assert.Single(all!).ReplaceAll);
        Assert.False(Assert.Single(one!).ReplaceAll);
    }

    [Fact]
    public void 記録の行から_Write_を読む_全体の置き換えになる()
    {
        var ops = ClaudeRequestDiff.ReadOperations(
            ToolUse("t1", "Write", new { file_path = "a.cs", content = "first\r\nsecond" }), "t1");

        Assert.Equal(new ClaudeEditOp(null, "first\nsecond", false, true), Assert.Single(ops!));
    }

    [Fact]
    public void 記録の行から_MultiEdit_を順に読む()
    {
        var ops = ClaudeRequestDiff.ReadOperations(
            ToolUse("t1", "MultiEdit", new
            {
                file_path = "a.cs",
                edits = new object[]
                {
                    new { old_string = "a", new_string = "b" },
                    new { old_string = "c", new_string = "d", replace_all = true },
                },
            }), "t1");

        Assert.NotNull(ops);
        Assert.Equal(2, ops!.Count);
        Assert.Equal(new ClaudeEditOp("a", "b", false, false), ops[0]);
        Assert.Equal(new ClaudeEditOp("c", "d", true, false), ops[1]);
    }

    [Fact]
    public void NotebookEdit_と_Read_と別の_id_と壊れた行は_null()
    {
        Assert.Null(ClaudeRequestDiff.ReadOperations(
            ToolUse("t1", "NotebookEdit", new { notebook_path = "a.ipynb", new_source = "print(1)" }), "t1"));
        Assert.Null(ClaudeRequestDiff.ReadOperations(ToolUse("t2", "Read", new { file_path = "a.cs" }), "t2"));
        Assert.Null(ClaudeRequestDiff.ReadOperations(
            ToolUse("t3", "Write", new { file_path = "a.cs", content = "x" }), "other"));
        Assert.Null(ClaudeRequestDiff.ReadOperations("{ not json", "t4"));
        Assert.Null(ClaudeRequestDiff.ReadOperations(Prompt("ただの頼みごと"), "t5"));
    }

    // ===== 抜粋（Excerpt） =====

    [Fact]
    public void 抜粋は操作ごとに区切られ_左右に同じ区切りが入る()
    {
        var (left, right) = ClaudeRequestDiff.Excerpt(
        [
            new ClaudeEditOp("old one", "new one", false, false),
            new ClaudeEditOp("old two\n", "new two\nand more", false, false),
        ]);

        Assert.Equal("old one\n⋯\nold two\n", left);
        Assert.Equal("new one\n⋯\nnew two\nand more\n", right);
    }

    [Fact]
    public void 抜粋は_Write_が来たらやり直す_左は空()
    {
        var (left, right) = ClaudeRequestDiff.Excerpt(
        [
            new ClaudeEditOp("old one", "new one", false, false),
            new ClaudeEditOp(null, "whole\nfile", false, true),
        ]);

        Assert.Equal("", left);
        Assert.Equal("whole\nfile\n", right);
    }

    // ===== Build：ファイル履歴から前を取る =====

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void delta_のバックアップから前を取り_2回の_Edit_を当て直した後が出る(bool historyBeforePrompt)
    {
        var file = Path.Combine(_repo, "a.cs");
        File.WriteAllText(file, AfterTwoEdits);
        Backup("s1", "abc@v1", Original);
        // ファイル履歴の行は、頼みごとの行の前にあっても後にあっても引ける。
        var head = historyBeforePrompt
            ? new[] { Delta("u1", "a.cs", "abc@v1"), Prompt("メソッドを足して名前を変えて", "u1") }
            : new[] { Prompt("メソッドを足して名前を変えて", "u1"), Delta("u1", "a.cs", "abc@v1") };
        WriteTranscript("s1", [.. head, .. TwoEdits(file)]);

        var diff = Assert.Single(BuildFor(file));

        Assert.Equal(Path.GetFullPath(file), diff.FilePath, ignoreCase: true);
        Assert.Equal(Original, diff.Before);
        Assert.Equal(AfterTwoEdits, diff.After);
        Assert.True(diff.Exact);
        Assert.False(diff.Created);
        Assert.Equal(2, diff.Edits);
        Assert.Equal(2, diff.AddedLines);
        Assert.Equal(2, diff.AddedLinesPresent);
        Assert.Equal(ClaudeRequestFileState.Unchanged, diff.Current);
    }

    [Fact]
    public void snapshot_の形でも前を取れる_相対パスは_cwd_から解決する()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "sub"));
        var file = Path.Combine(_repo, "sub", "a.cs");
        // バックアップもディスクも CRLF。前後は LF にそろえて比べる。
        File.WriteAllText(file, AfterTwoEdits.Replace("\n", "\r\n"));
        Backup("s1", "abc@v1", Original.Replace("\n", "\r\n"));
        WriteTranscript("s1",
        [
            Snapshot("u1", ("sub/a.cs", "abc@v1", 1)),
            Prompt("メソッドを足して名前を変えて", "u1"),
            .. TwoEdits(file),
        ]);

        var diff = Assert.Single(BuildFor(file));

        Assert.Equal(Original, diff.Before);
        Assert.Equal(AfterTwoEdits, diff.After);
        Assert.True(diff.Exact);
        Assert.False(diff.Created);
        Assert.Equal(2, diff.Edits);
        Assert.Equal(ClaudeRequestFileState.Unchanged, diff.Current);
    }

    [Fact]
    public void バックアップ無しと記録されていれば_この頼みごとが作ったファイル()
    {
        const string content = "class Fresh\n{\n    void Hello() { }\n}\n";
        var file = Path.Combine(_repo, "fresh.cs");
        File.WriteAllText(file, content);
        WriteTranscript("s1",
            Prompt("新しいクラスを作って", "u1"),
            Delta("u1", "fresh.cs", null),
            ToolUse("t1", "Write", new { file_path = file, content }),
            Result("t1", "ok"));

        var diff = Assert.Single(BuildFor(file));

        Assert.True(diff.Created);
        Assert.True(diff.Exact);
        Assert.Equal("", diff.Before);
        Assert.Equal(content, diff.After);
        Assert.Equal(1, diff.Edits);
        Assert.Equal(ClaudeRequestFileState.Unchanged, diff.Current);
    }

    [Fact]
    public void あとからファイルが変わっていれば_ChangedLater_足した行は中身のある行だけ数える()
    {
        var file = Path.Combine(_repo, "a.cs");
        Backup("s1", "abc@v1", Original);
        WriteTranscript("s1",
            Prompt("メソッドを足して名前を変えて", "u1"),
            Delta("u1", "a.cs", "abc@v1"),
            // 足した行は 3 行だが、「{」「}」は中身のある行ではない——数えるのは 1 行。
            ToolUse("t1", "Edit", new { file_path = file, old_string = "    void One() { }", new_string = "    void One() { }\n    void AddedFirst()\n    {\n    }" }),
            Result("t1", "ok"),
            ToolUse("t2", "Edit", new { file_path = file, old_string = "    void Two() { }", new_string = "    void TwoRenamed() { }" }),
            Result("t2", "ok"));
        const string after = "class A\n{\n    void One() { }\n    void AddedFirst()\n    {\n    }\n    void TwoRenamed() { }\n}\n";
        // そのあと人間が 1 行を書き換えた。
        File.WriteAllText(file, after.Replace("    void TwoRenamed() { }", "    void HumanRename() { }"));

        var diff = Assert.Single(BuildFor(file));

        Assert.True(diff.Exact);
        Assert.Equal(Original, diff.Before);
        Assert.Equal(after, diff.After);
        Assert.Equal(ClaudeRequestFileState.ChangedLater, diff.Current);
        Assert.Equal(2, diff.AddedLines);
        Assert.Equal(1, diff.AddedLinesPresent);
    }

    [Fact]
    public void ファイルがもう無ければ_Missing()
    {
        var file = Path.Combine(_repo, "a.cs");
        Backup("s1", "abc@v1", Original);
        WriteTranscript("s1", [Prompt("メソッドを足して名前を変えて", "u1"), Delta("u1", "a.cs", "abc@v1"), .. TwoEdits(file)]);
        Assert.False(File.Exists(file));

        var diff = Assert.Single(BuildFor(file));

        Assert.True(diff.Exact);
        Assert.Equal(AfterTwoEdits, diff.After);
        Assert.Equal(ClaudeRequestFileState.Missing, diff.Current);
        Assert.Equal(2, diff.AddedLines);
        Assert.Equal(0, diff.AddedLinesPresent);
    }

    [Fact]
    public void 同じ頼みごとに複数のバックアップがあれば_版のいちばん小さいものが前()
    {
        var file = Path.Combine(_repo, "a.cs");
        File.WriteAllText(file, AfterTwoEdits);
        Backup("s1", "abc@v1", Original);
        Backup("s1", "abc@v2", "class A\n{\n}\n"); // 途中の版。これを前にすると Edit が当たらない。
        WriteTranscript("s1",
        [
            Prompt("メソッドを足して名前を変えて", "u1"),
            Delta("u1", "a.cs", "abc@v2", version: 2),
            Delta("u1", "a.cs", "abc@v1", version: 1),
            .. TwoEdits(file),
        ]);

        var diff = Assert.Single(BuildFor(file));

        Assert.True(diff.Exact);
        Assert.Equal(Original, diff.Before);
        Assert.Equal(AfterTwoEdits, diff.After);
    }

    // ===== Build：ファイル履歴が無いとき =====

    [Fact]
    public void ファイル履歴が無ければ_最初の結果の_originalFile_が前になる()
    {
        var file = Path.Combine(_repo, "a.cs");
        File.WriteAllText(file, AfterTwoEdits);
        WriteTranscript("s1",
            Prompt("メソッドを足して名前を変えて"),
            ToolUse("t1", "Edit", new { file_path = file, old_string = "    void One() { }", new_string = "    void One() { }\n    void AddedFirst() { }" }),
            Result("t1", "ok", toolUseResult: new { filePath = file, originalFile = Original.Replace("\n", "\r\n") }),
            ToolUse("t2", "Edit", new { file_path = file, old_string = "    void Two() { }", new_string = "    void TwoRenamed() { }" }),
            // 2 つ目の結果の元の中身（＝1 つ目の後）は前にしない。
            Result("t2", "ok", toolUseResult: new { filePath = file, originalFile = "class A\n{\n    void One() { }\n    void AddedFirst() { }\n    void Two() { }\n}\n" }));

        var diff = Assert.Single(BuildFor(file));

        Assert.True(diff.Exact);
        Assert.False(diff.Created);
        Assert.Equal(Original, diff.Before);
        Assert.Equal(AfterTwoEdits, diff.After);
        Assert.Equal(2, diff.Edits);
        Assert.Equal(ClaudeRequestFileState.Unchanged, diff.Current);
    }

    [Fact]
    public void ファイル履歴が無く_Write_の結果が_create_なら_作ったファイル()
    {
        const string content = "class Fresh\n{\n    void Hello() { }\n}\n";
        var file = Path.Combine(_repo, "fresh.cs");
        File.WriteAllText(file, content);
        WriteTranscript("s1",
            Prompt("新しいクラスを作って"),
            ToolUse("t1", "Write", new { file_path = file, content }),
            Result("t1", "ok", toolUseResult: new { type = "create", filePath = file, content }));

        var diff = Assert.Single(BuildFor(file));

        Assert.True(diff.Created);
        Assert.True(diff.Exact);
        Assert.Equal("", diff.Before);
        Assert.Equal(content, diff.After);
        Assert.Equal(ClaudeRequestFileState.Unchanged, diff.Current);
    }

    [Fact]
    public void 前の中身がどこにも無ければ抜粋()
    {
        var file = Path.Combine(_repo, "a.cs");
        File.WriteAllText(file, AfterTwoEdits);
        WriteTranscript("s1", [Prompt("メソッドを足して名前を変えて", "u1"), .. TwoEdits(file)]);

        var diff = Assert.Single(BuildFor(file));

        Assert.False(diff.Exact);
        Assert.False(diff.Created);
        Assert.Equal(ClaudeRequestFileState.Unknown, diff.Current);
        Assert.Equal("    void One() { }\n⋯\n    void Two() { }\n", diff.Before);
        Assert.Equal("    void One() { }\n    void AddedFirst() { }\n⋯\n    void TwoRenamed() { }\n", diff.After);
        Assert.Equal(2, diff.Edits);
        Assert.Equal(2, diff.AddedLines);
        Assert.Equal(2, diff.AddedLinesPresent);
    }

    [Fact]
    public void バックアップがあっても_Edit_が当たらなければ抜粋に落ちる()
    {
        var file = Path.Combine(_repo, "a.cs");
        File.WriteAllText(file, "class A\n{\n    void TwoRenamed() { }\n}\n");
        Backup("s1", "abc@v1", "class A\n{\n}\n"); // 置換前（void Two）が無い
        WriteTranscript("s1",
            Prompt("名前を変えて", "u1"),
            Delta("u1", "a.cs", "abc@v1"),
            ToolUse("t1", "Edit", new { file_path = file, old_string = "    void Two() { }", new_string = "    void TwoRenamed() { }" }),
            Result("t1", "ok"));

        var diff = Assert.Single(BuildFor(file));

        Assert.False(diff.Exact);
        Assert.False(diff.Created);
        Assert.Equal(ClaudeRequestFileState.Unknown, diff.Current);
        Assert.Equal("    void Two() { }\n", diff.Before);
        Assert.Equal("    void TwoRenamed() { }\n", diff.After);
    }

    [Fact]
    public void フォルダーの外を指すバックアップの名前は読まない()
    {
        var file = Path.Combine(_repo, "a.cs");
        File.WriteAllText(file, AfterTwoEdits);
        // 名前をそのままつなぐと <file-history>\s1\..\evil ＝ <file-history>\evil を読んでしまう。
        Directory.CreateDirectory(Path.Combine(_history, "s1"));
        File.WriteAllText(Path.Combine(_history, "evil"), Original);
        WriteTranscript("s1", [Prompt("メソッドを足して名前を変えて", "u1"), Delta("u1", "a.cs", "..\\evil"), .. TwoEdits(file)]);

        var diff = Assert.Single(BuildFor(file));

        // 読めていれば Original から全体を組めてしまう。読まないので抜粋。
        Assert.False(diff.Exact);
        Assert.False(diff.Created);
        Assert.Equal(ClaudeRequestFileState.Unknown, diff.Current);
        Assert.DoesNotContain("class A", diff.Before);
    }

    // ===== Build：頼みごとの単位 =====

    [Fact]
    public void 同じファイルを書いた2つ目の頼みごとは_自分のバックアップから組まれ_1つ目の変更を含まない()
    {
        const string original = "class A\n{\n    void One() { }\n}\n";
        const string afterFirst = "class A\n{\n    void One() { }\n    void FromFirstRequest() { }\n}\n";
        const string afterSecond = "class A\n{\n    void OneRenamedBySecond() { }\n    void FromFirstRequest() { }\n}\n";
        var file = Path.Combine(_repo, "a.cs");
        File.WriteAllText(file, afterSecond);
        Backup("s1", "abc@v1", original);
        Backup("s1", "abc@v2", afterFirst);
        WriteTranscript("s1",
            Prompt("メソッドを足して", "u1"),
            Delta("u1", "a.cs", "abc@v1", version: 1),
            ToolUse("t1", "Edit", new { file_path = file, old_string = "    void One() { }", new_string = "    void One() { }\n    void FromFirstRequest() { }" }),
            Result("t1", "ok"),
            Prompt("名前を変えて", "u2"),
            Delta("u2", "a.cs", "abc@v2", version: 2),
            ToolUse("t2", "Edit", new { file_path = file, old_string = "    void One() { }", new_string = "    void OneRenamedBySecond() { }" }),
            Result("t2", "ok"));
        var index = new ClaudeProvenanceIndex(_workspace, () => _projects);
        var writes = index.WritesFor(file);
        Assert.Equal(["メソッドを足して", "名前を変えて"], writes.Select(write => write.Prompt));

        var second = Assert.Single(ClaudeRequestDiffBuilder.Build(index.WritesOfRequest(writes[1]), _history));
        var first = Assert.Single(ClaudeRequestDiffBuilder.Build(index.WritesOfRequest(writes[0]), _history));

        // 2 つ目：前は 1 つ目の後。1 つ目が足した行は前にも後にもあるので、差分には出ない。
        Assert.True(second.Exact);
        Assert.Equal(afterFirst, second.Before);
        Assert.Equal(afterSecond, second.After);
        Assert.Equal(1, second.Edits);
        Assert.Equal(1, second.AddedLines);
        Assert.Equal(ClaudeRequestFileState.Unchanged, second.Current);
        // 1 つ目：後は 2 つ目の変更を含まない。いまのファイルはそのあと変わっている。
        Assert.True(first.Exact);
        Assert.Equal(original, first.Before);
        Assert.Equal(afterFirst, first.After);
        Assert.Equal(1, first.Edits);
        Assert.Equal(ClaudeRequestFileState.ChangedLater, first.Current);
        Assert.Equal(1, first.AddedLines);
        Assert.Equal(1, first.AddedLinesPresent);
    }

    [Fact]
    public void 二つのファイルを書いた頼みごとは2件返る()
    {
        const string content = "class B\n{\n    void FromWrite() { }\n}\n";
        var a = Path.Combine(_repo, "a.cs");
        var b = Path.Combine(_repo, "b.cs");
        File.WriteAllText(a, "class A\n{\n    void One() { }\n    void TwoRenamed() { }\n}\n");
        File.WriteAllText(b, content);
        WriteTranscript("s1",
            Prompt("B を作って A の名前を変えて"),
            ToolUse("t1", "Write", new { file_path = b, content }),
            Result("t1", "ok", toolUseResult: new { type = "create", filePath = b, content }),
            ToolUse("t2", "Edit", new { file_path = a, old_string = "    void Two() { }", new_string = "    void TwoRenamed() { }" }),
            Result("t2", "ok", toolUseResult: new { filePath = a, originalFile = Original }));

        var diffs = BuildFor(a).OrderBy(diff => Path.GetFileName(diff.FilePath), StringComparer.Ordinal).ToList();

        Assert.Equal(["a.cs", "b.cs"], diffs.Select(diff => Path.GetFileName(diff.FilePath)));
        Assert.All(diffs, diff => Assert.True(diff.Exact));
        Assert.All(diffs, diff => Assert.Equal(1, diff.Edits));
        Assert.All(diffs, diff => Assert.Equal(ClaudeRequestFileState.Unchanged, diff.Current));
        Assert.False(diffs[0].Created);
        Assert.Equal(Original, diffs[0].Before);
        Assert.True(diffs[1].Created);
        Assert.Equal(content, diffs[1].After);
    }

    [Fact]
    public void 行を足さない_Edit_消すだけ_も当て直しに入る()
    {
        // 1 つ目は消すだけ（足した行が無い）。索引から落ちると、後に One が残ったままになる。
        const string after = "class A\n{\n    void TwoRenamed() { }\n}\n";
        var a = Path.Combine(_repo, "a.cs");
        File.WriteAllText(a, after);
        WriteTranscript("s1",
            Prompt("One を消して Two の名前を変えて"),
            ToolUse("t1", "Edit", new { file_path = a, old_string = "    void One() { }\n", new_string = "" }),
            Result("t1", "ok", toolUseResult: new { filePath = a, originalFile = Original }),
            ToolUse("t2", "Edit", new { file_path = a, old_string = "    void Two() { }", new_string = "    void TwoRenamed() { }" }),
            Result("t2", "ok"));

        var diff = Assert.Single(BuildFor(a));

        Assert.True(diff.Exact);
        Assert.Equal(2, diff.Edits);
        Assert.Equal(after, diff.After);
        Assert.Equal(ClaudeRequestFileState.Unchanged, diff.Current);
    }

    [Fact]
    public void 書き込みが無ければ空()
    {
        Assert.Empty(ClaudeRequestDiffBuilder.Build([], _history));
    }

    // ===== Diff ペインの素材（ClaudeRequestComparisons） =====

    [Fact]
    public void 素材は表示名の順に並び_そのままのファイルには注記が付かない()
    {
        var origin = Origin("s1", 7);
        var files = new[]
        {
            FileDiff(@"C:\ws\b.cs", "one\ntwo\n", "one\ntwo\nthree\n"),
            FileDiff(@"C:\ws\a.cs", "one\ntwo\n", "one\n"),
        };

        var comparisons = sk0ya.Loomo.App.Services.ClaudeRequestComparisons.Build(files, origin, path => Path.GetFileName(path));

        Assert.Equal(["a.cs", "b.cs"], comparisons.Select(comparison => comparison.Label));
        var b = comparisons[1];
        Assert.Equal(@"C:\ws\b.cs", b.FilePath);
        Assert.Equal("one\ntwo\n", b.LeftText);
        Assert.Equal("one\ntwo\nthree\n", b.RightText);
        Assert.Equal("頼みごとの前", b.LeftTitle);
        Assert.Equal("頼みごとの後（いまもこのまま）", b.RightTitle);
        Assert.Equal("", b.Badge);
        Assert.False(b.FileIsLeft);
        // 追加・削除の行数は先に数えてある。
        Assert.NotNull(b.Stat);
        Assert.Equal((1, 0), b.Stat!.Value);
        Assert.Equal((0, 1), comparisons[0].Stat!.Value);
        // 「見た」印は頼みごとごとの見方で覚える。
        Assert.All(comparisons, comparison => Assert.Equal("request:s1:7", comparison.ViewedScope));
    }

    [Fact]
    public void 作ったファイルの素材は_追加_の印が付く()
    {
        var files = new[] { FileDiff(@"C:\ws\fresh.cs", "", "class Fresh\n", created: true) };

        var comparison = Assert.Single(sk0ya.Loomo.App.Services.ClaudeRequestComparisons.Build(files, Origin("s1", 3), path => Path.GetFileName(path)));

        Assert.Equal("追加", comparison.Badge);
        Assert.Equal("fresh.cs", comparison.Label);
        Assert.Equal("頼みごとの前（無かった）", comparison.LeftTitle);
        Assert.Equal("request:s1:3", comparison.ViewedScope);
    }

    [Fact]
    public void あとから変わった_抜粋_いまは無い_は見出しで言う()
    {
        var files = new[]
        {
            FileDiff(@"C:\ws\a-changed.cs", "x\n", "y\n", current: ClaudeRequestFileState.ChangedLater, addedLines: 3, addedLinesPresent: 1),
            FileDiff(@"C:\ws\b-excerpt.cs", "x\n", "y\n", exact: false, current: ClaudeRequestFileState.Unknown),
            FileDiff(@"C:\ws\c-missing.cs", "x\n", "y\n", current: ClaudeRequestFileState.Missing),
            // 抜粋で、しかももう無い：見出しは「いまは無い」を言う。
            FileDiff(@"C:\ws\d-excerpt-missing.cs", "x\n", "y\n", exact: false, current: ClaudeRequestFileState.Missing),
        };

        var comparisons = sk0ya.Loomo.App.Services.ClaudeRequestComparisons.Build(files, Origin("s1", 7), path => Path.GetFileName(path));

        Assert.Equal(
            ["a-changed.cs  ※このあと変わった", "b-excerpt.cs  ※抜粋", "c-missing.cs  ※いまは無い", "d-excerpt-missing.cs  ※いまは無い"],
            comparisons.Select(comparison => comparison.Label));
        Assert.Equal("頼みごとの後（このあと変わった・足した 3 行のうち 1 行がいまも残る）", comparisons[0].RightTitle);
        Assert.Equal("置換前（抜粋）", comparisons[1].LeftTitle);
        Assert.Equal("置換後（抜粋・行番号はファイルのものではない）", comparisons[1].RightTitle);
        Assert.Equal("頼みごとの後（いまは無い）", comparisons[2].RightTitle);
        Assert.All(comparisons, comparison => Assert.Equal("", comparison.Badge));
    }

    // ===== 道具 =====

    /// <summary><paramref name="file"/> を書いた最後の頼みごとの差分（記録 → 索引 → Build）。</summary>
    private IReadOnlyList<ClaudeRequestFileDiff> BuildFor(string file)
    {
        var index = new ClaudeProvenanceIndex(_workspace, () => _projects);
        var writes = index.WritesFor(file);
        Assert.NotEmpty(writes);
        return ClaudeRequestDiffBuilder.Build(index.WritesOfRequest(writes[^1]), _history);
    }

    /// <summary><see cref="Original"/> を <see cref="AfterTwoEdits"/> にする 2 回の Edit（操作と結果の 4 行）。</summary>
    private string[] TwoEdits(string file) =>
    [
        ToolUse("t1", "Edit", new { file_path = file, old_string = "    void One() { }", new_string = "    void One() { }\n    void AddedFirst() { }" }),
        Result("t1", "ok"),
        ToolUse("t2", "Edit", new { file_path = file, old_string = "    void Two() { }", new_string = "    void TwoRenamed() { }" }),
        Result("t2", "ok"),
    ];

    private static ClaudeWrite Origin(string sessionId, int promptLine)
        => new(sessionId, "t.jsonl", "a.cs", "頼み", promptLine, null, Array.Empty<string>(), false);

    private static ClaudeRequestFileDiff FileDiff(
        string path, string before, string after, bool exact = true, bool created = false,
        ClaudeRequestFileState current = ClaudeRequestFileState.Unchanged, int addedLines = 0, int addedLinesPresent = 0)
        => new(path, before, after, exact, created, 1, addedLines, addedLinesPresent, current);

    private string WriteTranscript(string sessionId, params string[] lines)
    {
        var dir = Path.Combine(_projects, ClaudeTranscriptLocator.Slug(_repo));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, sessionId + ".jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    /// <summary>ファイル履歴のバックアップの実体（<c>&lt;file-history&gt;/&lt;セッション&gt;/&lt;名前&gt;</c>）。</summary>
    private void Backup(string sessionId, string name, string content)
    {
        var dir = Path.Combine(_history, sessionId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), content);
    }

    private string Stamp() => new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero).AddSeconds(++_clock).ToString("o");

    /// <summary>頼みごとの行。ファイル履歴はこの行の <paramref name="uuid"/> で引かれる。</summary>
    private string Prompt(string text, string? uuid = null)
        => JsonSerializer.Serialize(new { type = "user", uuid, timestamp = Stamp(), cwd = _repo, message = new { role = "user", content = text } });

    private string ToolUse(string id, string name, object input)
        => JsonSerializer.Serialize(new
        {
            type = "assistant",
            timestamp = Stamp(),
            message = new { role = "assistant", content = new object[] { new { type = "tool_use", id, name, input } } },
        });

    private string Result(string id, string text, bool isError = false, object? toolUseResult = null)
        => JsonSerializer.Serialize(new
        {
            type = "user",
            timestamp = Stamp(),
            message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = id, content = text, is_error = isError } } },
            toolUseResult,
        });

    /// <summary>ファイル履歴の行（差分の形）。<paramref name="backupFileName"/> が null なら「触る前は無かった」。</summary>
    private string Delta(string promptUuid, string trackingPath, string? backupFileName, int version = 1)
        => JsonSerializer.Serialize(new
        {
            type = "file-history-delta",
            messageId = "delta-" + (++_clock),
            snapshotMessageId = promptUuid,
            trackingPath,
            backup = new { backupFileName, version, backupTime = Stamp(), realParentDir = _repo },
        });

    /// <summary>ファイル履歴の行（まとめの形）。パスは cwd からの相対でよい。</summary>
    private string Snapshot(string promptUuid, params (string Path, string? Backup, int Version)[] files)
        => JsonSerializer.Serialize(new
        {
            type = "file-history-snapshot",
            messageId = promptUuid,
            snapshot = new
            {
                messageId = promptUuid,
                trackedFileBackups = files.ToDictionary(
                    file => file.Path,
                    file => (object)new { backupFileName = file.Backup, version = file.Version, backupTime = Stamp() }),
                timestamp = Stamp(),
            },
            isSnapshotUpdate = false,
        });
}
