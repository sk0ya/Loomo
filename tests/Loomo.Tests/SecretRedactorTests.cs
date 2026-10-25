using System;
using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Safety;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>§24.66 秘密をコマンドの記録とペグボードに残さない。</summary>
public sealed class SecretRedactorTests
{
    private static readonly DateTime T0 = new(2026, 10, 25, 3, 0, 0, DateTimeKind.Utc);

    // 実在しない値（形だけ合わせたもの）
    private const string GitHubToken = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";

    [Theory]
    [InlineData("$env:GITHUB_TOKEN = \"" + GitHubToken + "\"")]
    [InlineData("gh auth login --with-token " + GitHubToken)]
    [InlineData("github_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyz")]
    [InlineData("curl -H \"Authorization: Bearer abc.def.ghi\" https://api.example.com")]
    [InlineData("export OPENAI_API_KEY=sk-proj-abcdefghijklmnopqrstuvwx")]
    [InlineData("aws configure set aws_access_key_id AKIAABCDEFGHIJKLMNOP")]
    [InlineData("vault login hvs.CAESIabcdefghijklmnopqrstuvwxyz")]
    [InlineData("mysql --password=hunter2 -u root")]
    [InlineData("Invoke-Thing -Password 'hunter2'")]
    [InlineData("git clone https://me:hunter2@example.com/repo.git")]
    [InlineData("set DB_PASSWORD=hunter2")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijklmnop")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXk\n-----END OPENSSH PRIVATE KEY-----")]
    public void Finds_secrets(string text)
        => Assert.True(SecretRedactor.ContainsSecret(text));

    [Theory]
    [InlineData("dotnet build -p:Configuration=Release")]
    [InlineData("mkdir -p src/a/b")]
    [InlineData("git log --oneline -20")]
    [InlineData("$env:GITHUB_TOKEN = $(gh auth token)")]
    [InlineData("export GITHUB_TOKEN=$GH_TOKEN")]
    [InlineData("set API_KEY=%MY_KEY%")]
    [InlineData("Get-Content tokens.txt | Select-String password")]
    [InlineData("git push https://github.com/sk0ya/Loomo.git main")]
    [InlineData("cd C:\\Projects\\Loomo; dotnet test --filter Token")]
    [InlineData("4f9c1b2a7e8d3c6b5a49081726354f3e2d1c0b9a")]   // コミットのハッシュ
    public void Leaves_ordinary_commands_alone(string text)
        => Assert.False(SecretRedactor.ContainsSecret(text));

    [Fact]
    public void Redact_keeps_the_name_and_masks_only_the_value()
    {
        var redacted = SecretRedactor.Redact("$env:NUGET_API_KEY = \"oy2abc\"; mysql --password hunter2", out var count);
        Assert.Equal($"$env:NUGET_API_KEY = {SecretRedactor.Mask}; mysql --password {SecretRedactor.Mask}", redacted);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Redact_masks_whole_tokens_once()
    {
        var redacted = SecretRedactor.Redact($"token: {GitHubToken}\nnext line", out var count);
        Assert.Equal($"token: {SecretRedactor.Mask}\nnext line", redacted);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Redact_masks_url_password_but_keeps_user_and_host()
        => Assert.Equal($"https://me:{SecretRedactor.Mask}@example.com/r.git",
            SecretRedactor.Redact("https://me:hunter2@example.com/r.git"));

    // ===== 最近のコマンド =====

    [Fact]
    public void Recent_commands_do_not_record_a_line_with_a_secret()
        => Assert.Null(RecentTerminalCommands.Record(null,
            new RecentTerminalCommandRun($"$env:GITHUB_TOKEN=\"{GitHubToken}\"", 0, T0)));

    [Fact]
    public void Recent_commands_drop_secret_lines_recorded_by_an_older_version()
    {
        var current = new List<RecentTerminalCommandSnapshot>
        {
            new() { Command = $"gh auth login --with-token {GitHubToken}", RunCount = 1, LastRunUtc = T0 },
            new() { Command = "git status", RunCount = 1, LastRunUtc = T0 },
        };
        var updated = RecentTerminalCommands.Record(current, new RecentTerminalCommandRun("dotnet build", 0, T0))!;
        Assert.Equal(new[] { "dotnet build", "git status" }, updated.Select(c => c.Command));
    }

    // ===== ペグボード =====

    [Fact]
    public void Pegboard_output_is_masked_and_says_so()
    {
        var output = new TerminalCommandOutput($"gh auth token", $"{GitHubToken}\n", 0, T0, false, Guid.NewGuid(), null);
        var item = TerminalPegboardMaterial.FromOutput(output, "pwsh")!;
        Assert.Equal(SecretRedactor.Mask, item.Content);
        Assert.Equal(1, item.Source.MaskedSecrets);
        Assert.Contains("1 か所伏せました", TerminalPegboardMaterial.ToolTip(item.Source));
    }

    [Fact]
    public void Pegboard_selection_masks_the_command_of_the_containing_output_too()
    {
        var tab = Guid.NewGuid();
        var output = new TerminalCommandOutput("mysql --password=hunter2 -e \"select 1\"", "1\n", 0, T0, false, tab, null);
        var item = TerminalPegboardMaterial.FromSelection("1", tab, "pwsh", null, output, T0)!;
        Assert.Equal($"mysql --password={SecretRedactor.Mask} -e \"select 1\"", item.Source.Command);
        Assert.Equal(1, item.Source.MaskedSecrets);
    }

    [Fact]
    public void Pegboard_without_secrets_reports_nothing()
    {
        var output = new TerminalCommandOutput("dotnet test", "Passed!  - Failed: 0\n", 0, T0, false, Guid.NewGuid(), null);
        var item = TerminalPegboardMaterial.FromOutput(output, "pwsh")!;
        Assert.Equal(0, item.Source.MaskedSecrets);
        Assert.DoesNotContain("伏せました", TerminalPegboardMaterial.ToolTip(item.Source));
    }
}
