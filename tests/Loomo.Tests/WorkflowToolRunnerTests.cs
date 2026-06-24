using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>非AIツールステップを決定論実行する <see cref="WorkflowToolRunner"/> の検証。</summary>
public class WorkflowToolRunnerTests : IDisposable
{
    private readonly string _dir;
    private readonly RecordingTerminal _terminal = new();
    private readonly FakeEditorService _editor = new();
    private readonly WorkspaceService _workspace;
    private readonly WorkflowToolRunner _runner;

    public WorkflowToolRunnerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "loomo-wfrunner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _workspace = new WorkspaceService(new SafetySettings());
        _workspace.OpenFolder(_dir);
        _runner = new WorkflowToolRunner(_terminal, _workspace, _editor,
            new SafetyPolicy(new SafetySettings()));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static Task<WorkflowToolResult> Run(WorkflowToolRunner r, WorkflowStep step, string primary, string content = "")
        => r.RunAsync(step, primary, content, CancellationToken.None);

    [Fact]
    public async Task Command_step_returns_stdout_and_success()
    {
        _terminal.NextOutput = "hello world\n";
        var step = new WorkflowStep { Kind = WorkflowStepKind.Command };

        var result = await Run(_runner, step, "echo hello world");

        Assert.True(result.Ok);
        Assert.Equal("hello world", result.Output);
        Assert.Equal("echo hello world", _terminal.LastCommand);
    }

    [Fact]
    public async Task Command_step_blocked_by_safety_is_not_executed()
    {
        var step = new WorkflowStep { Kind = WorkflowStepKind.Command };

        var result = await Run(_runner, step, "Remove-Item -Recurse -Force C:\\important");

        Assert.False(result.Ok);
        Assert.Null(_terminal.LastCommand);   // 実行されていない
        Assert.Contains("ブロック", result.Summary);
    }

    [Fact]
    public async Task ReadFile_step_returns_file_content()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "in.txt"), "ファイル本文です");
        var step = new WorkflowStep { Kind = WorkflowStepKind.ReadFile };

        var result = await Run(_runner, step, "in.txt");

        Assert.True(result.Ok);
        Assert.Equal("ファイル本文です", result.Output);
    }

    [Fact]
    public async Task ReadFile_missing_file_is_error()
    {
        var step = new WorkflowStep { Kind = WorkflowStepKind.ReadFile };

        var result = await Run(_runner, step, "does-not-exist.txt");

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task WriteFile_step_writes_content_and_opens_editor()
    {
        var step = new WorkflowStep { Kind = WorkflowStepKind.WriteFile };

        var result = await Run(_runner, step, "out.txt", "書き出す内容");

        Assert.True(result.Ok);
        Assert.Equal("書き出す内容", await File.ReadAllTextAsync(Path.Combine(_dir, "out.txt")));
        Assert.Equal("書き出す内容", result.Output);   // 後段へ渡せるよう内容を返す
    }

    [Fact]
    public async Task Transform_literal_replace()
    {
        var step = new WorkflowStep { Kind = WorkflowStepKind.Transform, Pattern = "foo" };

        var result = await Run(_runner, step, "foo bar foo", "X");

        Assert.True(result.Ok);
        Assert.Equal("X bar X", result.Output);
    }

    [Fact]
    public async Task Transform_regex_replace()
    {
        var step = new WorkflowStep { Kind = WorkflowStepKind.Transform, Pattern = @"\d+", IsRegex = true };

        var result = await Run(_runner, step, "a12b345", "#");

        Assert.True(result.Ok);
        Assert.Equal("a#b#", result.Output);
    }

    [Fact]
    public async Task Transform_invalid_regex_is_error()
    {
        var step = new WorkflowStep { Kind = WorkflowStepKind.Transform, Pattern = "(", IsRegex = true };

        var result = await Run(_runner, step, "anything", "x");

        Assert.False(result.Ok);
    }

    private sealed class RecordingTerminal : ITerminalService
    {
        public string? LastCommand { get; private set; }
        public string NextOutput { get; set; } = "";
        public int NextExit { get; set; }

        public string CurrentDirectory => "C:\\ws";
        public bool IsExecuting => false;

        public Task<CommandResult> RunCommandAsync(string command, CancellationToken ct)
        {
            LastCommand = command;
            return Task.FromResult(new CommandResult(command, NextOutput, NextExit, CurrentDirectory, NextExit == 0));
        }

        public void SetWorkingDirectory(string path) { }
        public bool TryRunInVisibleTerminal(string command) => false;
#pragma warning disable CS0067
        public event EventHandler<CommandResult>? CommandExecuted;
#pragma warning restore CS0067
    }
}
