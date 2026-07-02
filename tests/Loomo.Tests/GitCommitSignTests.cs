using System.IO;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// <see cref="GitService.CommitAsync(string, bool, bool)"/> の sign 引数（GPG署名 <c>-S</c>）を検証する。
/// 実際の git プロセスを起動する既存テスト（<see cref="GitMergeStrategyTests"/> 等）と同じ方式だが、
/// 「署名が成立すること」自体はテストしない（実行環境に GPG 鍵があるとは限らないため）。
/// gpg.program を存在しないコマンドへわざと差し替えることで、鍵の有無に関係なく
/// 「-S が実際に git コマンドへ渡ったこと」だけを決定的に確認する。
/// </summary>
public sealed class GitCommitSignTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-git-commit-sign-tests", Guid.NewGuid().ToString("N"));
    private GitService _git = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        _git = new GitService(workspace);
        await MustRunAsync("init");
        await MustRunAsync("config", "user.name", "Loomo Test");
        await MustRunAsync("config", "user.email", "loomo@example.invalid");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* Git がファイルを解放するまでの競合は無視 */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task signがfalseなら通常どおりコミットできる()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "unsigned.txt"), "content");
        await MustRunAsync("add", "unsigned.txt");

        var result = await _git.CommitAsync("unsigned commit", amend: false, sign: false);

        Assert.True(result.Success, result.Error);
        var log = await MustRunAsync("log", "--oneline");
        Assert.Contains("unsigned commit", log.Output);
    }

    [Fact]
    public async Task signがtrueなら_Sオプションが渡りgpgprogramが不正だと署名に失敗してコミットされない()
    {
        // 鍵の有無に依存せず決定的に失敗させる：gpg.program を存在しないコマンドへ差し替える。
        await MustRunAsync("config", "gpg.program", "loomo-test-nonexistent-gpg-stub");
        await File.WriteAllTextAsync(Path.Combine(_root, "signed.txt"), "content");
        await MustRunAsync("add", "signed.txt");

        var result = await _git.CommitAsync("signed commit", amend: false, sign: true);

        Assert.False(result.Success);
        // 署名失敗でコミットされていない＝まだ1つも commit が無い（HEAD が存在しない）はずなので、
        // ここは MustRunAsync（成功前提）ではなく素の RunAsync で確認する。
        var log = await _git.RunAsync("log", "--oneline");
        Assert.DoesNotContain("signed commit", log.Output);
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, result.Error);
        return result;
    }
}
