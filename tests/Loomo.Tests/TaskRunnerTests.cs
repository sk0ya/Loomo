using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.App.Input;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tasks;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 汎用タスクランナー（§31.17）——tasks.json（JSONC）の読み取り、組み込み problem matcher の照合
/// （MSBuild / tsc / eslint stylish・compact / gcc）、可視ターミナルの折り返しの継ぎ直し、パスの解決、
/// コマンド行の組み立て、dependsOn の実行順、実行と Problems（発生源 Task）への流し込みの検証。
/// </summary>
public class TaskRunnerTests
{
    private const string Ws = @"C:\work\app";
    private const string Ws2 = @"C:\work\lib";

    private static WorkspaceTask OnlyTask(string json, string folder = Ws)
        => Assert.Single(TasksJsonParser.Parse(json, folder).Tasks);

    private static ProblemMatchContext Context(string? cwd = null, Func<string, bool>? exists = null)
        => new(Ws, cwd ?? Ws, exists ?? (_ => false));

    // ===== tasks.json の読み取り =====

    [Fact]
    public void Parses_jsonc_with_comments_and_trailing_commas()
    {
        var file = TasksJsonParser.Parse("""
            // VS Code が生成する tasks.json はコメント付き
            {
              "version": "2.0.0",
              /* ブロックコメントも */
              "tasks": [
                {
                  "label": "build",
                  "type": "shell",
                  "command": "dotnet",
                  "args": ["build", "My App.sln",],
                  "group": { "kind": "build", "isDefault": true },
                  "problemMatcher": "$msCompile",
                },
              ],
            }
            """, Ws);

        Assert.Empty(file.Warnings);
        var task = Assert.Single(file.Tasks);
        Assert.Equal("build", task.Label);
        Assert.Equal(WorkspaceTaskKind.Shell, task.Kind);
        Assert.Equal("dotnet", task.Command);
        Assert.Equal(new[] { "build", "My App.sln" }, task.Args);
        Assert.True(task.IsDefaultBuild);
        Assert.Same(BuiltInProblemMatchers.MsCompile, Assert.Single(task.Matchers));
        Assert.Equal(Ws, task.WorkspaceFolder);
    }

    [Fact]
    public void Invalid_json_yields_warning_not_exception()
    {
        var file = TasksJsonParser.Parse("{ \"tasks\": [ { \"label\": ", Ws);

        Assert.Empty(file.Tasks);
        Assert.Contains("tasks.json を読めません", Assert.Single(file.Warnings));
    }

    [Fact]
    public void Reads_group_string_process_type_cwd_background_and_dependsOn()
    {
        var file = TasksJsonParser.Parse("""
            {
              "options": { "cwd": "${workspaceFolder}/src" },
              "tasks": [
                { "label": "lint", "type": "process", "command": "eslint", "args": ["."], "group": "test",
                  "dependsOn": "prep", "detail": "静的解析" },
                { "label": "prep", "command": "echo prep", "options": { "cwd": "tools" } },
                { "label": "watch", "command": "tsc -w", "isBackground": true },
                { "label": "all", "dependsOn": ["prep", "lint"], "dependsOrder": "sequence" }
              ]
            }
            """, Ws);

        Assert.Empty(file.Warnings);
        var lint = file.Tasks[0];
        Assert.Equal(WorkspaceTaskKind.Process, lint.Kind);
        Assert.Equal("test", lint.Group);
        Assert.False(lint.IsDefault);
        Assert.Equal("${workspaceFolder}/src", lint.Cwd);   // 最上位の options.cwd を引き継ぐ
        Assert.Equal(new[] { "prep" }, lint.DependsOn);
        Assert.Equal("静的解析", lint.Detail);
        Assert.Equal("tools", file.Tasks[1].Cwd);            // タスク側の指定が勝つ
        Assert.True(file.Tasks[2].IsBackground);
        Assert.Null(file.Tasks[3].Command);                  // dependsOn だけの複合タスク
        Assert.Equal(new[] { "prep", "lint" }, file.Tasks[3].DependsOn);
    }

    [Fact]
    public void Windows_override_replaces_command_and_args()
    {
        var task = OnlyTask("""
            { "tasks": [ { "label": "b", "command": "make", "args": ["all"],
                           "windows": { "command": "nmake", "args": ["/f", "Makefile.win"] } } ] }
            """);

        Assert.Equal("nmake", task.Command);
        Assert.Equal(new[] { "/f", "Makefile.win" }, task.Args);
    }

    [Fact]
    public void Npm_task_becomes_npm_cmd_run_in_package_folder()
    {
        var task = OnlyTask("""{ "tasks": [ { "type": "npm", "script": "build", "path": "web/" } ] }""");

        Assert.Equal("npm: build", task.Label);
        Assert.Equal("npm.cmd run build", task.Command);
        Assert.Equal("${workspaceFolder}/web", task.Cwd);
    }

    [Fact]
    public void Unsupported_entries_are_skipped_with_reasons()
    {
        var file = TasksJsonParser.Parse("""
            { "tasks": [
                { "label": "ext", "type": "gulp", "task": "default" },
                { "label": "x", "command": "a", "problemMatcher": ["$nope", "$tsc"], "options": { "env": { "A": "1" } } },
                { "label": "x", "command": "b" },
                { "label": "empty" }
            ] }
            """, Ws);

        var task = Assert.Single(file.Tasks);
        Assert.Equal("a", task.Command);
        Assert.Same(BuiltInProblemMatchers.Tsc, Assert.Single(task.Matchers));   // 未知の名前だけ落とす
        Assert.Contains(file.Warnings, w => w.Contains("gulp"));
        Assert.Contains(file.Warnings, w => w.Contains("$nope"));
        Assert.Contains(file.Warnings, w => w.Contains("options.env"));
        Assert.Contains(file.Warnings, w => w.Contains("重複"));
        Assert.Contains(file.Warnings, w => w.Contains("empty"));
    }

    [Fact]
    public void Inline_matcher_reads_pattern_fileLocation_severity_and_owner()
    {
        var task = OnlyTask("""
            { "tasks": [ { "label": "py", "command": "flake8",
              "problemMatcher": {
                "owner": "flake8",
                "fileLocation": ["relative", "${workspaceFolder}/pkg"],
                "severity": "warning",
                "pattern": { "regexp": "^(.+):(\\d+):(\\d+): (\\w+\\d+) (.*)$",
                             "file": 1, "line": 2, "column": 3, "code": 4, "message": 5 }
              } } ] }
            """);

        var matcher = Assert.Single(task.Matchers);
        Assert.Equal("flake8", matcher.Owner);
        Assert.Equal(ProblemFileLocation.Relative, matcher.FileLocation);

        var problem = Assert.Single(ProblemMatcherEngine.Match(
            "mod/a.py:3:1: E302 expected 2 blank lines", task.Matchers, Context()));
        Assert.Equal(@"C:\work\app\pkg\mod\a.py", problem.FilePath);
        Assert.Equal(TaskProblemSeverity.Warning, problem.Severity);   // severity グループ無し→既定
        Assert.Equal("E302", problem.Code);
        Assert.Equal((3, 1), (problem.Line, problem.Column));
    }

    [Fact]
    public void Inline_single_pattern_uses_vscode_defaults_for_omitted_groups()
    {
        // file=1・location=2・message=4（location 指定時）が省略時の既定。
        var task = OnlyTask("""
            { "tasks": [ { "label": "t", "command": "x",
              "problemMatcher": { "fileLocation": "absolute", "pattern": { "regexp": "^(.*)\\((\\d+,\\d+)\\) (\\w+): (.*)$" } } } ] }
            """);

        var problem = Assert.Single(ProblemMatcherEngine.Match(@"C:\a\b.txt(4,2) bad: 何か変", task.Matchers, Context()));
        Assert.Equal(@"C:\a\b.txt", problem.FilePath);
        Assert.Equal((4, 2), (problem.Line, problem.Column));
        Assert.Equal("何か変", problem.Message);
    }

    [Fact]
    public void Base_matcher_can_be_overridden_by_fileLocation()
    {
        var task = OnlyTask("""
            { "tasks": [ { "label": "t", "command": "tsc", "problemMatcher": { "base": "$tsc", "fileLocation": ["relative", "${workspaceFolder}/web"] } } ] }
            """);

        var problem = Assert.Single(ProblemMatcherEngine.Match(
            "src/a.ts(1,2): error TS1005: ';' expected.", task.Matchers, Context(cwd: @"C:\elsewhere")));
        Assert.Equal(@"C:\work\app\web\src\a.ts", problem.FilePath);
    }

    [Fact]
    public void Invalid_regex_is_reported()
    {
        var file = TasksJsonParser.Parse("""
            { "tasks": [ { "label": "t", "command": "x", "problemMatcher": { "pattern": { "regexp": "([" } } } ] }
            """, Ws);

        Assert.Empty(Assert.Single(file.Tasks).Matchers);
        Assert.Contains(file.Warnings, w => w.Contains("regexp"));
    }

    // ===== 組み込み matcher =====

    [Fact]
    public void MsCompile_reads_classic_node_prefixed_indented_and_fatal_lines()
    {
        var output = string.Join("\r\n",
            "  復元対象のプロジェクトを決定しています...",
            @"C:\work\app\src\A.cs(12,34): error CS1002: ; expected [C:\work\app\src\App.csproj]",
            @"  3>C:\work\app\src\B.cs(5,1): warning CS0219: The variable 'x' is assigned [C:\work\app\src\App.csproj]",
            @"    C:\work\app\src\C.cs(7,2,7,9): error CS0103: The name 'y' does not exist",
            @"C:\work\app\src\D.cs(1,1): fatal error CS0001: boom",
            "ビルドに失敗しました。",
            @"C:\work\app\src\A.cs(12,34): error CS1002: ; expected [C:\work\app\src\App.csproj]");   // サマリの再掲

        var problems = ProblemMatcherEngine.Match(output, [BuiltInProblemMatchers.MsCompile], Context());

        Assert.Equal(4, problems.Count);
        Assert.Equal(@"C:\work\app\src\A.cs", problems[0].FilePath);
        Assert.Equal("; expected", problems[0].Message);   // 末尾の [proj] は落とす
        Assert.Equal("CS1002", problems[0].Code);
        Assert.Equal(TaskProblemSeverity.Warning, problems[1].Severity);
        Assert.Equal(@"C:\work\app\src\B.cs", problems[1].FilePath);
        Assert.Equal((7, 2, 7, 9), (problems[2].Line, problems[2].Column, problems[2].EndLine, problems[2].EndColumn));
        Assert.Equal(TaskProblemSeverity.Error, problems[3].Severity);
    }

    [Fact]
    public void MsCompile_handles_parentheses_in_path()
    {
        var problem = Assert.Single(ProblemMatcherEngine.Match(
            @"C:\Program Files (x86)\x\A.cs(3,4): error CS1001: Identifier expected",
            [BuiltInProblemMatchers.MsCompile], Context()));

        Assert.Equal(@"C:\Program Files (x86)\x\A.cs", problem.FilePath);
        Assert.Equal((3, 4), (problem.Line, problem.Column));
    }

    [Fact]
    public void Tsc_reads_both_formats_relative_to_task_cwd()
    {
        var output = string.Join("\n",
            "src/a.ts(7,5): error TS2322: Type 'string' is not assignable to type 'number'.",
            "src/b.ts:3:1 - warning TS6133: 'x' is declared but its value is never read.",
            "",
            "Found 2 errors.");

        var problems = ProblemMatcherEngine.Match(output, [BuiltInProblemMatchers.Tsc], Context(cwd: @"C:\work\app\web"));

        Assert.Equal(2, problems.Count);
        Assert.Equal(@"C:\work\app\web\src\a.ts", problems[0].FilePath);
        Assert.Equal("TS2322", problems[0].Code);
        Assert.Equal((7, 5), (problems[0].Line, problems[0].Column));
        Assert.Equal(@"C:\work\app\web\src\b.ts", problems[1].FilePath);
        Assert.Equal(TaskProblemSeverity.Warning, problems[1].Severity);
    }

    [Fact]
    public void EslintStylish_reads_multiline_groups_per_file()
    {
        var output = string.Join("\n",
            "",
            @"C:\work\app\src\a.js",
            "   3:7   error    'x' is assigned a value but never used  no-unused-vars",
            "  10:1   warning  Unexpected console statement            no-console",
            "",
            @"C:\work\app\src\b.js",
            "  1:15  error  Missing semicolon  semi",
            "",
            "✖ 3 problems (2 errors, 1 warning)",
            "  1 error and 0 warnings potentially fixable with the `--fix` option.");

        var problems = ProblemMatcherEngine.Match(output, [BuiltInProblemMatchers.EslintStylish], Context());

        Assert.Equal(3, problems.Count);
        Assert.All(problems.Take(2), p => Assert.Equal(@"C:\work\app\src\a.js", p.FilePath));
        Assert.Equal((3, 7), (problems[0].Line, problems[0].Column));
        Assert.Equal("'x' is assigned a value but never used", problems[0].Message);
        Assert.Equal("no-unused-vars", problems[0].Code);
        Assert.Equal(TaskProblemSeverity.Warning, problems[1].Severity);
        Assert.Equal("no-console", problems[1].Code);
        Assert.Equal(@"C:\work\app\src\b.js", problems[2].FilePath);
        Assert.Equal("semi", problems[2].Code);
    }

    [Fact]
    public void EslintStylish_relative_paths_are_autodetected()
    {
        var output = "src/a.js\n  2:1  error  Bad  rule";
        var problem = Assert.Single(ProblemMatcherEngine.Match(output, [BuiltInProblemMatchers.EslintStylish],
            Context(cwd: @"C:\work\app\web", exists: p => p == @"C:\work\app\web\src\a.js")));

        // 基準（ワークスペース）で見つからず、作業フォルダーで見つかった方を採る。
        Assert.Equal(@"C:\work\app\web\src\a.js", problem.FilePath);
    }

    [Fact]
    public void EslintCompact_reads_line()
    {
        var problem = Assert.Single(ProblemMatcherEngine.Match(
            @"C:\work\app\a.js: line 4, col 2, Warning - Unexpected var (no-var)",
            [BuiltInProblemMatchers.EslintCompact], Context()));

        Assert.Equal((4, 2), (problem.Line, problem.Column));
        Assert.Equal(TaskProblemSeverity.Warning, problem.Severity);
        Assert.Equal("no-var", problem.Code);
        Assert.Equal("Unexpected var", problem.Message);
    }

    [Fact]
    public void Gcc_reads_windows_paths_notes_and_missing_column()
    {
        var output = string.Join("\n",
            @"C:\work\app\src\main.c:12:5: error: 'y' undeclared (first use in this function)",
            "   12 |     y = 1;",
            "      |     ^",
            "src/util.c:3:10: fatal error: missing.h: No such file or directory",
            "src/util.c:8:: warning: unused variable 'z'",
            "src/util.c:9:1: note: declared here");

        var problems = ProblemMatcherEngine.Match(output, [BuiltInProblemMatchers.Gcc], Context());

        Assert.Equal(4, problems.Count);
        Assert.Equal(@"C:\work\app\src\main.c", problems[0].FilePath);
        Assert.Equal((12, 5), (problems[0].Line, problems[0].Column));
        Assert.Null(problems[0].Code);
        Assert.Equal("cpp", problems[0].Owner);
        Assert.Equal(@"C:\work\app\src\util.c", problems[1].FilePath);   // 見つからなければワークスペース基準
        Assert.Equal(TaskProblemSeverity.Error, problems[1].Severity);
        Assert.Equal((8, 1), (problems[2].Line, problems[2].Column));    // 列なし→1
        Assert.Equal(TaskProblemSeverity.Information, problems[3].Severity);
    }

    // ===== 可視ターミナルの折り返し =====

    [Fact]
    public void Soft_wrapped_msbuild_line_is_rejoined()
    {
        const int width = 100;
        var full = @"C:\work\app\src\Very\Deep\Folder\Structure\Component.cs(12,34): error CS1002: ; expected [C:\work\app\src\App.csproj]";
        var rows = new List<string>();
        for (var i = 0; i < full.Length; i += width) rows.Add(full.Substring(i, Math.Min(width, full.Length - i)));
        rows.Add("    0 Warning(s)");
        Assert.Equal(2, rows.Count - 1);

        var problem = Assert.Single(ProblemMatcherEngine.Match(string.Join("\r\n", rows),
            [BuiltInProblemMatchers.MsCompile], Context()));

        Assert.Equal("; expected", problem.Message);
        Assert.Equal(@"C:\work\app\src\Very\Deep\Folder\Structure\Component.cs", problem.FilePath);
    }

    [Fact]
    public void Rejoin_does_not_swallow_next_record_or_indented_line()
    {
        var a = new string('a', 70);
        var lines = new[] { a, "next-record", a, "  indented", a, "tail" };

        var result = ProblemMatcherEngine.RejoinSoftWraps(lines, line => line == "next-record");

        Assert.Equal(new[] { a, "next-record", a, "  indented", a + "tail" }, result);
    }

    [Fact]
    public void Rejoin_skips_narrow_outputs()
    {
        var lines = new[] { "short line", "continues?" };
        Assert.Equal(lines, ProblemMatcherEngine.RejoinSoftWraps(lines, _ => false));
    }

    // ===== コマンド行の組み立て =====

    [Fact]
    public void Shell_task_quotes_only_args_that_need_it_and_moves_to_cwd()
    {
        var task = OnlyTask("""
            { "tasks": [ { "label": "b", "command": "dotnet build", "args": ["My App.sln", "-c", "Release", "it's"],
                           "options": { "cwd": "${workspaceFolder}/src" } } ] }
            """);

        var invocation = TaskCommandBuilder.Build(task, null);

        Assert.Equal(@"C:\work\app\src", invocation.Cwd);
        Assert.Equal("dotnet build 'My App.sln' -c Release 'it''s'", invocation.CommandLine);
        Assert.Equal(@"Set-Location -LiteralPath 'C:\work\app\src'; dotnet build 'My App.sln' -c Release 'it''s'",
            invocation.TerminalLine);
    }

    [Fact]
    public void Process_task_quotes_everything_and_uses_call_operator()
    {
        var task = OnlyTask("""{ "tasks": [ { "label": "p", "type": "process", "command": "C:\\Tools\\my tool.exe", "args": ["a", "${workspaceFolderBasename}"] } ] }""");

        Assert.Equal(@"& 'C:\Tools\my tool.exe' 'a' 'app'", TaskCommandBuilder.Build(task, null).CommandLine);
    }

    [Fact]
    public void File_variables_expand_from_active_file_and_require_one()
    {
        var task = OnlyTask("""{ "tasks": [ { "label": "f", "command": "node", "args": ["${relativeFile}", "${fileBasenameNoExtension}"], "options": { "cwd": "${fileDirname}" } } ] }""");

        var invocation = TaskCommandBuilder.Build(task, @"C:\work\app\src\main.js");
        Assert.Equal(@"node src\main.js main", invocation.CommandLine);
        Assert.Equal(@"C:\work\app\src", invocation.Cwd);

        var ex = Assert.Throws<TaskConfigurationException>(() => TaskCommandBuilder.Build(task, null));
        Assert.Contains("エディタで開いて", ex.Message);
    }

    [Fact]
    public void Input_variables_without_an_answer_are_refused_instead_of_blanked()
    {
        var task = OnlyTask("""{ "tasks": [ { "label": "i", "command": "deploy ${input:target}" } ] }""");

        Assert.Throws<TaskConfigurationException>(() => TaskCommandBuilder.Build(task, null));
        var ex = Assert.Throws<TaskConfigurationException>(() => TaskInputs.Required([task]));
        Assert.Contains("inputs にありません", ex.Message);
    }

    // ===== inputs（§31.17.6） =====

    private const string InputsJson = """
        {
          "inputs": [
            { "id": "env", "type": "pickString", "description": "デプロイ先", "default": "stg",
              "options": [ "dev", { "label": "ステージング", "value": "stg" }, "prod" ] },
            { "id": "msg", "type": "promptString", "description": "メッセージ", "default": "hi" },
            { "id": "token", "type": "promptString", "password": true },
            { "id": "ext", "type": "command", "command": "extension.pick" },
            { "type": "promptString" },
            { "id": "bad", "type": "pickString" },
            { "id": "odd", "type": "quickPick" },
          ],
          "tasks": [
            { "label": "deploy", "command": "deploy", "args": ["--env", "${input:env}", "--msg", "${input:msg}"],
              "options": { "cwd": "${workspaceFolder}/${input:env}" }, "dependsOn": "build" },
            { "label": "build", "command": "build ${input:env}" },
            { "label": "ext", "command": "x ${input:ext}" },
          ]
        }
        """;

    [Fact]
    public void Inputs_are_read_per_file_with_options_defaults_and_warnings()
    {
        var file = TasksJsonParser.Parse(InputsJson, Ws);
        var inputs = file.Tasks[0].Inputs;

        Assert.Equal(new[] { "env", "msg", "token", "ext" }, inputs.Keys);
        var env = inputs["env"];
        Assert.Equal(TaskInputKind.PickString, env.Kind);
        Assert.Equal("デプロイ先", env.Prompt);
        Assert.Equal(new[] { ("dev", "dev"), ("ステージング", "stg"), ("prod", "prod") },
            env.Options.Select(o => (o.Label, o.Value)));
        Assert.True(inputs["token"].Password);
        Assert.Equal("token", inputs["token"].Prompt);   // description が無ければ id で訊く
        Assert.Equal(TaskInputKind.Command, inputs["ext"].Kind);
        Assert.Same(inputs, file.Tasks[1].Inputs);       // 同じ tasks.json のタスクは同じ定義を見る
        Assert.Equal(3, file.Warnings.Count(w => w.Contains("id の無い") || w.Contains("options がありません") || w.Contains("quickPick")));
    }

    [Fact]
    public void Pick_options_put_the_default_first()
    {
        var env = TasksJsonParser.Parse(InputsJson, Ws).Tasks[0].Inputs["env"];

        Assert.Equal(new[] { "stg", "dev", "prod" }, TaskInputs.OrderedOptions(env).Select(o => o.Value));
    }

    [Fact]
    public void Required_inputs_are_unique_in_first_use_order_across_dependencies_and_refuse_command_type()
    {
        var all = TasksJsonParser.Parse(InputsJson, Ws).Tasks;
        var deploy = all.Single(t => t.Label == "deploy");

        var required = TaskInputs.Required(TaskPlanner.Order(deploy, all));
        Assert.Equal(new[] { "env", "msg" }, required.Select(i => i.Id));   // build の env と deploy の env は1回だけ訊く

        var ex = Assert.Throws<TaskConfigurationException>(() => TaskInputs.Required([all.Single(t => t.Label == "ext")]));
        Assert.Contains("command", ex.Message);
    }

    [Fact]
    public void Answers_expand_in_command_args_and_cwd()
    {
        var all = TasksJsonParser.Parse(InputsJson, Ws).Tasks;
        var deploy = all.Single(t => t.Label == "deploy");
        var values = new TaskInputValues();
        values.Set(deploy.Inputs["env"], "stg");
        values.Set(deploy.Inputs["msg"], "hello world");

        var invocation = TaskCommandBuilder.Build(deploy, null, values);

        Assert.Equal(@"C:\work\app\stg", invocation.Cwd);
        Assert.Equal("deploy --env stg --msg 'hello world'", invocation.CommandLine);   // 答えは引数として引用される
    }

    [Fact]
    public async Task Runner_asks_each_input_once_before_typing_anything()
    {
        var terminal = new ScriptedTerminal(
            ("Set-Location -LiteralPath 'C:\\work\\app'; build prod", "", 0),
            ("Set-Location -LiteralPath 'C:\\work\\app\\prod'; deploy --env prod --msg ok", "", 0));
        var all = TasksJsonParser.Parse(InputsJson, Ws).Tasks;
        var asked = new List<string>();

        var result = await new WorkspaceTaskRunner(terminal).RunAsync(all.Single(t => t.Label == "deploy"), all, null,
            askInput: (input, _) =>
            {
                Assert.Equal(0, terminal.Calls);   // 1つも打ち込む前に全部訊く
                asked.Add(input.Id);
                return Task.FromResult<string?>(input.Id == "env" ? "prod" : "ok");
            });

        Assert.True(result.Success, result.Error);
        Assert.Equal(new[] { "env", "msg" }, asked);
        Assert.Equal(2, terminal.Calls);
    }

    [Fact]
    public async Task Cancelling_an_input_runs_nothing()
    {
        var terminal = new ScriptedTerminal();
        var all = TasksJsonParser.Parse(InputsJson, Ws).Tasks;
        var runner = new WorkspaceTaskRunner(terminal);

        var result = await runner.RunAsync(all.Single(t => t.Label == "deploy"), all, null,
            askInput: (_, _) => Task.FromResult<string?>(null));

        Assert.Contains("取り消した", result.Error);
        Assert.Equal(0, terminal.Calls);
        Assert.False(runner.IsRunning);   // 取り消しても次のタスクを走らせられる
    }

    // ===== dependsOn =====

    private static WorkspaceTask T(string label, string folder = Ws, params string[] depends)
        => new() { Label = label, WorkspaceFolder = folder, Command = "echo " + label, DependsOn = depends };

    [Fact]
    public void Planner_orders_dependencies_first_once_each()
    {
        var a = T("a"); var b = T("b", Ws, "a"); var c = T("c", Ws, "a", "b");

        Assert.Equal(new[] { "a", "b", "c" }, TaskPlanner.Order(c, [a, b, c]).Select(t => t.Label));
    }

    [Fact]
    public void Planner_prefers_same_folder_and_reports_cycles_and_missing()
    {
        var libPrep = T("prep", Ws2); var appPrep = T("prep", Ws); var build = T("build", Ws, "prep");
        Assert.Same(appPrep, TaskPlanner.Order(build, [libPrep, appPrep, build])[0]);

        var x = T("x", Ws, "y"); var y = T("y", Ws, "x");
        Assert.Contains("循環", Assert.Throws<TaskConfigurationException>(() => TaskPlanner.Order(x, [x, y])).Message);
        Assert.Contains("見つかりません", Assert.Throws<TaskConfigurationException>(
            () => TaskPlanner.Order(T("z", Ws, "nope"), [])).Message);
    }

    // ===== 一覧と既定のビルドタスク =====

    [Fact]
    public void Sort_puts_default_build_of_preferred_folder_first()
    {
        var other = T("other");
        var test = T("test") with { Group = "test" };
        var build = T("build") with { Group = "build" };
        var appDefault = T("app") with { Group = "build", IsDefault = true };
        var libDefault = T("lib", Ws2) with { Group = "build", IsDefault = true };
        var all = new[] { other, test, build, appDefault, libDefault };

        Assert.Equal(new[] { "lib", "app", "build", "test", "other" },
            WorkspaceTaskCatalog.Sort(all, Ws2).Select(t => t.Label));
        Assert.Same(libDefault, WorkspaceTaskCatalog.DefaultBuild(all, Ws2));
        Assert.Same(appDefault, WorkspaceTaskCatalog.DefaultBuild(all, Ws));
    }

    [Fact]
    public void DefaultBuild_falls_back_to_single_build_group_only()
    {
        var b1 = T("b1") with { Group = "build" };
        var b2 = T("b2") with { Group = "build" };

        Assert.Same(b1, WorkspaceTaskCatalog.DefaultBuild([b1, T("x")], Ws));
        Assert.Null(WorkspaceTaskCatalog.DefaultBuild([b1, b2], Ws));
    }

    [Fact]
    public void Catalog_loads_tasks_from_every_workspace_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "loomo-tasks-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(root, "app");
        var lib = Path.Combine(root, "lib");
        try
        {
            Directory.CreateDirectory(Path.Combine(app, ".vscode"));
            Directory.CreateDirectory(Path.Combine(lib, ".vscode"));
            File.WriteAllText(Path.Combine(app, ".vscode", "tasks.json"), """{ "tasks": [ { "label": "build", "command": "a" } ] }""");
            File.WriteAllText(Path.Combine(lib, ".vscode", "tasks.json"), """{ "tasks": [ { "label": "build", "command": "b", "type": "grunt" } ] }""");

            var file = WorkspaceTaskCatalog.Load([app, lib, Path.Combine(root, "missing")]);

            var task = Assert.Single(file.Tasks);
            Assert.Equal(app, task.WorkspaceFolder);
            Assert.StartsWith("lib: ", Assert.Single(file.Warnings));   // マルチルートは警告にフォルダー名
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // ===== 実行 =====

    [Fact]
    public async Task Runner_runs_dependencies_in_visible_terminal_and_matches_each_output()
    {
        var terminal = new ScriptedTerminal(
            ("Set-Location -LiteralPath 'C:\\work\\app'; echo gen", "", 0),
            ("Set-Location -LiteralPath 'C:\\work\\app\\web'; npx tsc --noEmit",
             "src/a.ts(1,2): error TS1005: ';' expected.", 2));
        var gen = T("gen");
        var tsc = new WorkspaceTask
        {
            Label = "tsc", WorkspaceFolder = Ws, Command = "npx tsc --noEmit", Cwd = "web",
            Matchers = [BuiltInProblemMatchers.Tsc], DependsOn = ["gen"],
        };
        var stepsSeen = new List<string>();

        var result = await new WorkspaceTaskRunner(terminal).RunAsync(tsc, [gen, tsc], null,
            step => stepsSeen.Add(step.Task.Label));

        Assert.Equal(new[] { "gen", "tsc" }, stepsSeen);
        Assert.False(result.Success);
        var problem = Assert.Single(result.Steps[1].Problems);
        Assert.Equal(@"C:\work\app\web\src\a.ts", problem.FilePath);   // $tsc は ${cwd}＝タスクの作業フォルダー基準
        Assert.Equal("タスク「tsc」: 失敗（終了コード 2）・エラー 1 / 警告 0（「問題」タブ）", result.Summary());
    }

    [Fact]
    public async Task Runner_stops_after_failed_dependency()
    {
        var terminal = new ScriptedTerminal(("Set-Location -LiteralPath 'C:\\work\\app'; echo gen", "", 1));
        var gen = T("gen");
        var build = T("build", Ws, "gen");

        var result = await new WorkspaceTaskRunner(terminal).RunAsync(build, [gen, build], null);

        Assert.Single(result.Steps);
        Assert.Equal(1, terminal.Calls);
        Assert.Equal("タスク「build」: 「gen」で失敗（終了コード 1）", result.Summary());
    }

    [Fact]
    public async Task Runner_refuses_background_dependency_and_config_errors_before_running()
    {
        var terminal = new ScriptedTerminal();
        var watch = T("watch") with { IsBackground = true };
        var build = T("build", Ws, "watch");

        var result = await new WorkspaceTaskRunner(terminal).RunAsync(build, [watch, build], null);

        Assert.Contains("バックグラウンド", result.Error);
        Assert.Equal(0, terminal.Calls);
    }

    [Fact]
    public async Task Runner_starts_background_task_without_waiting()
    {
        var terminal = new ScriptedTerminal();
        var watch = T("watch") with { IsBackground = true };

        var result = await new WorkspaceTaskRunner(terminal).RunAsync(watch, [watch], null);

        Assert.True(Assert.Single(result.Steps).Detached);
        Assert.Equal(new[] { "Set-Location -LiteralPath 'C:\\work\\app'; echo watch" }, terminal.Fired);
        Assert.Equal(0, terminal.Calls);
    }

    // ===== watch（§31.17.5）：サイクルごとに問題を差し替える =====

    /// <summary>実物の <c>tsc --watch --pretty</c>（TypeScript 5.6）を端末で走らせたときの出力（色を落としたもの）。
    /// 2エラー → 1つ直して1エラー → 全部直して0エラーの3サイクル。</summary>
    private static readonly string[] TscWatchOutput =
    [
        "[10:51:09] Starting compilation in watch mode...", "",
        "src/a.ts:1:7 - error TS2322: Type 'string' is not assignable to type 'number'.", "",
        "1 const a: number = \"x\";", "        ~", "",
        "src/a.ts:2:7 - error TS2322: Type 'number' is not assignable to type 'string'.", "",
        "2 const b: string = 1;", "        ~", "",
        "[10:51:10] Found 2 errors. Watching for file changes.", "",
        "[10:51:15] File change detected. Starting incremental compilation...", "",
        "src/a.ts:2:7 - error TS2322: Type 'number' is not assignable to type 'string'.", "",
        "2 const b: string = 1;", "        ~", "",
        "[10:51:16] Found 1 error. Watching for file changes.", "",
        "[10:51:19] File change detected. Starting incremental compilation...", "",
        "[10:51:19] Found 0 errors. Watching for file changes.",
    ];

    [Fact]
    public void Tsc_watch_problems_are_replaced_per_compilation_cycle()
    {
        var published = new List<IReadOnlyList<TaskProblem>>();
        var collector = new BackgroundProblemCollector([BuiltInProblemMatchers.Find("$tsc-watch")!],
            Context(cwd: @"C:\work\app\web"), published.Add);

        foreach (var line in TscWatchOutput) collector.OnLine(line);

        Assert.Equal([2, 1, 0], published.Select(p => p.Count));
        var first = published[0][0];
        Assert.Equal(@"C:\work\app\web\src\a.ts", first.FilePath);   // $tsc はタスクの作業フォルダー相対
        Assert.Equal((1, 7, "TS2322"), (first.Line, first.Column, first.Code));
        Assert.Equal("Type 'number' is not assignable to type 'string'.", published[1][0].Message);
    }

    [Fact]
    public void Unfinished_cycle_is_not_published_when_the_watch_stops()
    {
        var published = new List<IReadOnlyList<TaskProblem>>();
        var collector = new BackgroundProblemCollector([BuiltInProblemMatchers.Find("$tsc-watch")!],
            Context(), published.Add);
        foreach (var line in TscWatchOutput.Take(14)) collector.OnLine(line);   // 1サイクル目まで
        collector.OnLine("[10:51:15] File change detected. Starting incremental compilation...");
        collector.Complete();   // Ctrl+C：2サイクル目は途中

        Assert.Single(published);   // 途中までの出力で問題を減らさない（前のサイクルの結果を残す）
    }

    [Fact]
    public void Matcher_without_background_is_applied_once_when_the_watch_ends()
    {
        var published = new List<IReadOnlyList<TaskProblem>>();
        var collector = new BackgroundProblemCollector([BuiltInProblemMatchers.Tsc], Context(), published.Add);
        foreach (var line in TscWatchOutput) collector.OnLine(line);
        Assert.Empty(published);

        collector.Complete();
        Assert.Equal(2, Assert.Single(published).Count);   // 同じ診断の再掲は1件にまとまる
    }

    [Fact]
    public void Parses_inline_background_patterns_and_rejects_broken_ones()
    {
        var task = OnlyTask("""
            { "tasks": [ { "label": "dev", "command": "vite", "isBackground": true,
              "problemMatcher": { "owner": "vite", "pattern": { "regexp": "^(.*):(\\d+):(\\d+) (.*)$", "file": 1, "line": 2, "column": 3, "message": 4 },
                "background": { "activeOnStart": true, "beginsPattern": "rebuilding", "endsPattern": { "regexp": "built in \\d+ms" } } } } ] }
            """);
        var background = Assert.Single(task.Matchers).Background;
        Assert.NotNull(background);
        Assert.True(background.ActiveOnStart);
        Assert.Matches(background.EndsPattern!, "built in 120ms");

        var broken = TasksJsonParser.Parse("""
            { "tasks": [ { "label": "dev", "command": "vite", "isBackground": true,
              "problemMatcher": { "base": "$tsc", "background": { "beginsPattern": "(" } } } ] }
            """, Ws);
        Assert.Empty(Assert.Single(broken.Tasks).Matchers);
        Assert.Contains(broken.Warnings, w => w.Contains("beginsPattern"));
    }

    [Fact]
    public async Task Runner_follows_a_watch_and_holds_the_terminal_until_it_ends()
    {
        var terminal = new ScriptedTerminal();
        var runner = new WorkspaceTaskRunner(terminal);
        var watch = T("watch") with { IsBackground = true, Matchers = [BuiltInProblemMatchers.Find("$tsc-watch")!] };
        var build = T("build");
        var problems = new List<IReadOnlyList<TaskProblem>>();
        int? finished = -1;

        var result = await runner.RunAsync(watch, [watch, build], null,
            onWatchProblems: (_, p) => problems.Add(p), onWatchFinished: (_, code) => finished = code);
        Assert.True(Assert.Single(result.Steps).Detached);
        Assert.Same(watch, runner.ActiveWatch);

        foreach (var line in TscWatchOutput.Take(14)) terminal.WatchLine!(line);
        Assert.Equal(2, Assert.Single(problems).Count);

        // watch が端末を握っている間は、次のタスクを打ち込まない（watch の標準入力に入ってしまう）。
        var refused = await runner.RunAsync(build, [watch, build], null);
        Assert.Contains("Ctrl+C", refused.Error);
        Assert.Equal(0, terminal.Calls);

        terminal.WatchFinished!(130);
        Assert.Equal(130, finished);
        Assert.True(terminal.WatchDisposed);
        Assert.Null(runner.ActiveWatch);
    }

    [Fact]
    public async Task Stop_watching_releases_the_terminal_without_stopping_the_command()
    {
        var terminal = new ScriptedTerminal();
        var runner = new WorkspaceTaskRunner(terminal);
        var watch = T("watch") with { IsBackground = true };
        await runner.RunAsync(watch, [watch], null);

        runner.StopWatching();

        Assert.Null(runner.ActiveWatch);
        Assert.True(terminal.WatchDisposed);
    }

    [Fact]
    public async Task Runner_allows_one_run_at_a_time()
    {
        var gate = new TaskCompletionSource<CommandResult>();
        var terminal = new ScriptedTerminal { Pending = gate.Task };
        var runner = new WorkspaceTaskRunner(terminal);
        var task = T("slow");

        var first = runner.RunAsync(task, [task], null);
        Assert.True(runner.IsRunning);
        var second = await runner.RunAsync(task, [task], null);
        gate.SetResult(new CommandResult("", "", 0, Ws, true));
        await first;

        Assert.Contains("別のタスク", second.Error);
        Assert.False(runner.IsRunning);
    }

    // ===== Problems（発生源 Task） =====

    private static TaskProblem P(string file, int line, string? code = "X1", TaskProblemSeverity severity = TaskProblemSeverity.Error)
        => new(file, line, 1, line, 1, severity, code, "msg " + line, "owner");

    [Fact]
    public void Problems_task_source_is_replaced_per_task_and_kept_across_tasks()
    {
        var vm = new ProblemsViewModel();
        vm.SetTaskProblems("app|build", [P(@"C:\work\app\A.cs", 1), P(@"C:\work\app\A.cs", 2)]);
        vm.SetTaskProblems("app|lint", [P(@"C:\work\app\B.js", 3, null, TaskProblemSeverity.Warning)]);

        Assert.Equal(2, vm.ErrorCount);
        Assert.Equal(1, vm.WarningCount);
        var lint = vm.Groups.Single(g => g.FileName == "B.js").Items.Single();
        Assert.Equal(ProblemSource.Task, lint.Source);
        Assert.Equal("Task", lint.SourceLabel);
        Assert.Equal("owner", lint.Code);   // コードが無ければ matcher の owner で代用
        Assert.False(lint.HasCode);

        vm.SetTaskProblems("app|build", [P(@"C:\work\app\A.cs", 9)]);   // 走らせ直し＝丸ごと差し替え
        Assert.Equal(1, vm.ErrorCount);
        Assert.Equal(9, vm.Groups.Single(g => g.FileName == "A.cs").Items.Single().Line1);

        vm.SetTaskProblems("app|build", []);                           // きれいになった
        Assert.Equal(0, vm.ErrorCount);
        Assert.Equal(1, vm.WarningCount);

        vm.ShowTask = false;
        Assert.False(vm.HasItems);
        vm.ShowTask = true;
        vm.ClearTaskProblems();
        Assert.False(vm.HasItems);
    }

    [Fact]
    public void Problems_build_copy_wins_over_identical_task_copy()
    {
        var vm = new ProblemsViewModel();
        vm.SetFromBuildOutput(@"C:\work\app\A.cs(1,1): error X1: msg 1");
        vm.SetTaskProblems("app|build", [P(@"C:\work\app\A.cs", 1)]);

        var item = vm.Groups.Single().Items.Single();
        Assert.Equal(ProblemSource.Build, item.Source);
    }

    // ===== 入口（カタログ・パレット・キー結線の三点同期） =====

    [Theory]
    [InlineData("tasks.run")]
    [InlineData("tasks.runDefaultBuild")]
    public void Task_commands_are_in_catalog_palette_and_keybindings(string id)
    {
        var descriptor = CommandCatalog.Find(id);
        Assert.NotNull(descriptor);
        Assert.Null(descriptor!.DefaultBinding);   // Ctrl+Shift+B は型定義へ移動が使っている

        var palette = ReadSource("src", "Loomo.App", "Views", "Shell", "Navigation", "ShellWindow.CommandPalette.cs");
        var keys = ReadSource("src", "Loomo.App", "Views", "Shell", "Settings", "ShellWindow.Keybindings.cs");
        Assert.Contains($"Sc(\"{id}\"), \"{id}\"", palette.Replace("\r", "").Replace("\n", "").Replace("            ", " "));
        Assert.Contains($"[\"{id}\"] =", keys);
    }

    private static string ReadSource(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    private static string RepoRoot([CallerFilePath] string sourceFile = "")
    {
        var root = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "sk0ya.Loomo.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        return root!.FullName;
    }

    /// <summary>可視ターミナルの代役。送られた行を順に照合し、決めた出力と終了コードを返す。</summary>
    private sealed class ScriptedTerminal(params (string Line, string Output, int Exit)[] script) : ITerminalService
    {
        private int _next;
        public int Calls => _next;
        public List<string> Fired { get; } = new();
        public Task<CommandResult>? Pending { get; init; }
        public string CurrentDirectory => Ws;
        public bool IsExecuting => false;

        public Task<CommandResult> RunCommandAsync(string command, CancellationToken ct)
            => throw new InvalidOperationException("タスクは裏のプロセスで走らせない");

        public Task<CommandResult> RunCommandInVisibleTerminalAsync(string command, CancellationToken ct)
        {
            if (Pending is not null) { _next++; return Pending; }
            Assert.True(_next < script.Length, $"想定外のコマンド: {command}");
            var (line, output, exit) = script[_next++];
            Assert.Equal(line, command);
            return Task.FromResult(new CommandResult(command, output, exit, Ws, exit == 0));
        }

        public void SetWorkingDirectory(string path) { }
        public bool TryRunInVisibleTerminal(string command) { Fired.Add(command); return true; }

        // watch の購読（Terminal の SubscribeOutputLines の代役）。テストが行と終了を手で流す。
        public Action<string>? WatchLine { get; private set; }
        public Action<int?>? WatchFinished { get; private set; }
        public bool WatchDisposed { get; private set; }

        public IDisposable? StartWatchedInVisibleTerminal(string command, Action<string> lineReceived,
            Action<int?> commandFinished)
        {
            Fired.Add(command);
            WatchLine = lineReceived;
            WatchFinished = commandFinished;
            WatchDisposed = false;
            return new Subscription(() => WatchDisposed = true);
        }

        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
#pragma warning disable CS0067
        public event EventHandler<CommandResult>? CommandExecuted;
#pragma warning restore CS0067
    }
}
