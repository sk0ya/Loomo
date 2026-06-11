using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;
using sk0ya.Loomo.Core.Tools.Implementations;

namespace sk0ya.Loomo.Tests;

/// <summary>同一ファイル read|write パイプの footgun ガード：
/// `Get-Content x | … | Set-Content x` は実行前に回復可能エラーで差し戻し（ファイル破壊を防ぐ）、
/// 安全な形（括弧で全読み・変数経由・別ファイル）はそのまま実行する。PwshTool 経由で検証する。</summary>
public class PwshSameFilePipeGuardTests
{
    private static async Task<(ToolResult Result, string? Executed)> RunAsync(string command)
    {
        var terminal = new CapturingTerminal();
        var tool = new PwshTool(terminal);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { command }));
        var result = await tool.ExecuteAsync(doc.RootElement, CancellationToken.None);
        return (result, terminal.LastCommand);
    }

    [Theory]
    [InlineData("Get-Content src/util.txt | Where-Object { $_ -ne 'beta' } | Set-Content src/util.txt -Force")]
    [InlineData("Get-Content src/util.txt | ForEach-Object { $_.ToUpper() } | Out-File src/util.txt")]
    [InlineData("Get-Content a.txt | Add-Content a.txt")]
    [InlineData("Get-Content \"src/util.txt\" | Set-Content src\\util.txt")]   // クオート・区切り違いも同一視
    [InlineData("get-content A.TXT | set-content a.txt")]                      // 大文字小文字も同一視
    [InlineData("Get-Content -Path src/util.txt | Set-Content -Path src/util.txt -Value $c")]
    public async Task Same_file_read_write_pipe_is_blocked_before_execution(string command)
    {
        var (result, executed) = await RunAsync(command);

        Assert.True(result.IsError);
        Assert.Contains("edit_file", result.Content);
        Assert.Contains("変数へ読み込んでから", result.Content);
        Assert.Null(executed);   // 実行前にブロック＝ターミナルへ届かない
    }

    [Theory]
    [InlineData("(Get-Content a.txt) | Set-Content a.txt")]                       // 括弧で全読み→安全
    [InlineData("$c = Get-Content a.txt; Set-Content a.txt -Value $c")]           // 変数経由→安全
    [InlineData("Get-Content a.txt | Set-Content b.txt")]                         // 別ファイル
    [InlineData("Get-Content a.txt | Measure-Object -Line")]                      // 書き込み無し
    [InlineData("Get-Content a.txt")]                                             // パイプ無し
    [InlineData("Set-Content a.txt -Value 'x'")]                                  // 読み無し
    [InlineData("Get-ChildItem | Out-File list.txt")]                             // Get-Content 以外の読み
    public async Task Safe_commands_pass_through_to_terminal(string command)
    {
        var (result, executed) = await RunAsync(command);

        Assert.False(result.IsError);
        Assert.Equal(command, executed);
    }

    [Fact]
    public async Task Missing_path_failure_gets_mkdir_recovery_hint()
    {
        // copy-file 型の実故障：途中フォルダ不存在で Copy-Item が失敗すると、小モデルは
        // 「フォルダを作成してください」とユーザーへ投げ返す。次の一手をエラー本文に機械的に示す。
        var terminal = new FailingTerminal(
            "[stderr] Copy-Item : パス 'C:\\ws\\docs\\readme-copy.md' の一部が見つかりませんでした。");
        var tool = new PwshTool(terminal);
        using var doc = JsonDocument.Parse("{\"command\":\"Copy-Item README.md docs/readme-copy.md\"}");

        var result = await tool.ExecuteAsync(doc.RootElement, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("New-Item -ItemType Directory", result.Content);
    }

    [Fact]
    public async Task Rg_files_misuse_failure_gets_content_search_hint()
    {
        // find-text 型の実故障：`rg --files <パターン>` はパターンをパス扱いして os error 2 で失敗し、
        // 小モデルはこれを「該当ファイルなし」と誤読して虚偽の「見つかりませんでした」を回答する。
        // --files を外した正形をエラー本文に機械的に示す。
        var terminal = new FailingTerminal(
            "rg: gamma: 指定されたファイルが見つかりません。 (os error 2)\n.\\todo.md\n.\\src\\util.txt");
        var tool = new PwshTool(terminal);
        using var doc = JsonDocument.Parse("{\"command\":\"rg --files \\\"gamma\\\" .\"}");

        var result = await tool.ExecuteAsync(doc.RootElement, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("--files は検索パターンを取らず", result.Content);
        Assert.Contains("rg \"gamma\" .", result.Content);   // --files を外した実行例を提示
    }

    [Fact]
    public async Task Rg_files_with_matches_failure_gets_no_files_hint()
    {
        // --files-with-matches は正当な用法（パターンを取る）。パス間違い等で失敗しても
        // 「--files はパターンを取らない」ヒントを誤って出さない。
        var terminal = new FailingTerminal(
            "rg: bad-dir: 指定されたファイルが見つかりません。 (os error 2)");
        var tool = new PwshTool(terminal);
        using var doc = JsonDocument.Parse("{\"command\":\"rg --files-with-matches gamma bad-dir\"}");

        var result = await tool.ExecuteAsync(doc.RootElement, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.DoesNotContain("検索パターンを取らず", result.Content);
    }

    [Fact]
    public async Task Unrelated_failure_gets_no_recovery_hint()
    {
        var terminal = new FailingTerminal("[stderr] なにか別のエラー");
        var tool = new PwshTool(terminal);
        using var doc = JsonDocument.Parse("{\"command\":\"Get-ChildItem missing\"}");

        var result = await tool.ExecuteAsync(doc.RootElement, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.DoesNotContain("New-Item", result.Content);
    }

    /// <summary>常に失敗して固定の出力を返すターミナル（エラーヒント付与の検証用）。</summary>
    private sealed class FailingTerminal : ITerminalService
    {
        private readonly string _output;
        public FailingTerminal(string output) => _output = output;
        public string CurrentDirectory => "C:\\ws";
        public bool IsExecuting => false;

        public Task<CommandResult> RunCommandAsync(string command, CancellationToken ct)
            => Task.FromResult(new CommandResult(command, _output, 1, CurrentDirectory, false));

        public void SetWorkingDirectory(string path) { }

#pragma warning disable CS0067
        public event EventHandler<CommandResult>? CommandExecuted;
#pragma warning restore CS0067
    }

    private sealed class CapturingTerminal : ITerminalService
    {
        public string? LastCommand { get; private set; }
        public string CurrentDirectory => "C:\\Projects\\Loomo";
        public bool IsExecuting => false;

        public Task<CommandResult> RunCommandAsync(string command, CancellationToken ct)
        {
            LastCommand = command;
            return Task.FromResult(new CommandResult(command, "", 0, CurrentDirectory, true));
        }

        public void SetWorkingDirectory(string path) { }

#pragma warning disable CS0067
        public event EventHandler<CommandResult>? CommandExecuted;
#pragma warning restore CS0067
    }
}
