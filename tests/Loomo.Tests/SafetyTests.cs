using System.IO;
using System.Text.Json;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>安全設計（設計書 §10）: ブロックリスト・自動承認・ワークスペーススコープ制限。</summary>
public class SafetyTests
{
    private static JsonElement Args(string command)
        => JsonDocument.Parse($$"""{"command": {{JsonSerializer.Serialize(command)}}}""").RootElement;

    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("rm  -fr  node_modules")]
    [InlineData("shutdown /s")]
    [InlineData("Remove-Item C:\\x -Recurse -Force")]
    public void Blocks_dangerous_commands(string command)
    {
        var policy = new SafetyPolicy(new SafetySettings());
        var decision = policy.Evaluate("pwsh", Args(command));
        Assert.True(decision.Blocked);
        Assert.NotNull(decision.Reason);
    }

    [Theory]
    [InlineData("dotnet build")]
    [InlineData("dotnet format")]      // ディスクフォーマット誤検知の回帰防止
    [InlineData("git format-patch -1")]
    [InlineData("git status")]
    [InlineData("ls -la")]
    public void Allows_safe_commands(string command)
    {
        var policy = new SafetyPolicy(new SafetySettings());
        Assert.False(policy.Evaluate("pwsh", Args(command)).Blocked);
    }

    [Fact]
    public void Non_command_tools_are_not_evaluated()
    {
        var policy = new SafetyPolicy(new SafetySettings());
        Assert.False(policy.Evaluate("read_file", Args("rm -rf /")).Blocked);
    }

    [Fact]
    public void AutoApprove_reflects_settings()
    {
        var settings = new SafetySettings { AutoApprove = true };
        Assert.True(new SafetyPolicy(settings).AutoApprove);
    }

    [Fact]
    public void Workspace_resolves_relative_paths_under_root()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var ws = new WorkspaceService(new SafetySettings());
        ws.OpenFolder(root);

        var resolved = ws.ResolvePath("sub/file.txt");
        Assert.StartsWith(root, resolved);
    }

    [Fact]
    public void Workspace_blocks_paths_outside_root_when_restricted()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var ws = new WorkspaceService(new SafetySettings { RestrictToWorkspaceRoot = true });
        ws.OpenFolder(root);

        Assert.Throws<System.UnauthorizedAccessException>(() => ws.ResolvePath("../escape.txt"));
        Assert.Throws<System.UnauthorizedAccessException>(() => ws.ResolvePath(@"C:\Windows\System32"));
    }

    [Fact]
    public void Workspace_allows_outside_paths_when_unrestricted()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var ws = new WorkspaceService(new SafetySettings { RestrictToWorkspaceRoot = false });
        ws.OpenFolder(root);

        var resolved = ws.ResolvePath(@"C:\Windows");
        Assert.Equal(@"C:\Windows", resolved);
    }
}
