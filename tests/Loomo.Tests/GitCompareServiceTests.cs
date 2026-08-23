using System.IO;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 実際の git に対する比較基準の解決・一覧・差分。既存の git テスト（GitServiceHistoryTests 等）と
/// 同じく一時リポジトリを作って回す。空リポジトリ・デタッチ HEAD・ブランチ不在・無関係な履歴で
/// 壊れず理由が出ることもここで確かめる。
/// </summary>
public sealed class GitCompareServiceTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "loomo-git-compare", Guid.NewGuid().ToString("N"));
    private GitService _git = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        _git = new GitService(workspace);
        await MustRunAsync("init");
        // 既定ブランチ名は git のバージョン・設定で変わる（init -b は 2.28+ を要求する）。
        // 未出生 HEAD の付け替えなら古い git でも通り、警告も出ない。
        await MustRunAsync("symbolic-ref", "HEAD", "refs/heads/main");
        await MustRunAsync("config", "user.name", "Loomo Test");
        await MustRunAsync("config", "user.email", "loomo@example.invalid");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* git の解放待ちは無視 */ }
        return Task.CompletedTask;
    }

    // ===== 基準の解決 =====

    [Fact]
    public async Task 作業ツリー基準はgitを引かずそのまま返る()
    {
        var resolution = await _git.ResolveCompareBaseAsync(GitCompareBaseSelection.WorkingTree);

        Assert.Null(resolution.BaseRef);
        Assert.Null(resolution.Error);
        Assert.False(resolution.IsBaseComparison);
    }

    [Fact]
    public async Task 既定ブランチはリモート追跡が無くてもmainを選ぶ()
    {
        await CommitAsync("a.txt", "a", "first");

        Assert.Equal("main", await _git.GetDefaultBranchAsync());
        Assert.Contains("main", await _git.GetComparableRefsAsync());
    }

    [Fact]
    public async Task 存在しないブランチは理由付きで失敗する()
    {
        await CommitAsync("a.txt", "a", "first");

        var resolution = await _git.ResolveCompareBaseAsync(
            new GitCompareBaseSelection(GitCompareBaseKind.Branch, "nope"));

        Assert.Null(resolution.BaseRef);
        Assert.Contains("nope", resolution.Error);
    }

    [Fact]
    public async Task ブランチ未選択も理由付きで失敗する()
    {
        var resolution = await _git.ResolveCompareBaseAsync(
            new GitCompareBaseSelection(GitCompareBaseKind.MergeBase, null));

        Assert.Null(resolution.BaseRef);
        Assert.False(string.IsNullOrWhiteSpace(resolution.Error));
    }

    [Fact]
    public async Task 空リポジトリの分岐点は理由付きで失敗する()
    {
        // コミット0件＝HEAD が無い。ブランチも無いので、まず「ブランチが見つかりません」で止まる。
        var resolution = await _git.ResolveCompareBaseAsync(
            new GitCompareBaseSelection(GitCompareBaseKind.MergeBase, "main"));

        Assert.Null(resolution.BaseRef);
        Assert.False(string.IsNullOrWhiteSpace(resolution.Error));
    }

    [Fact]
    public async Task 無関係な履歴では分岐点が無いと理由が出る()
    {
        await CommitAsync("a.txt", "a", "first");
        // --orphan は親を持たない新しい履歴を作る＝共通の祖先が無い。
        await MustRunAsync("checkout", "--orphan", "alien");
        await _git.RunAsync("rm", "-rf", ".");
        await CommitAsync("z.txt", "z", "alien root");

        var resolution = await _git.ResolveCompareBaseAsync(
            new GitCompareBaseSelection(GitCompareBaseKind.MergeBase, "main"));

        Assert.Null(resolution.BaseRef);
        Assert.Contains("分岐点", resolution.Error);
    }

    [Fact]
    public async Task デタッチHEADでも分岐点は解決できる()
    {
        var first = await CommitAsync("a.txt", "a", "first");
        await CommitAsync("b.txt", "b", "second");
        await MustRunAsync("checkout", "--detach", first);

        var resolution = await _git.ResolveCompareBaseAsync(
            new GitCompareBaseSelection(GitCompareBaseKind.MergeBase, "main"));

        Assert.Null(resolution.Error);
        Assert.Equal(first, resolution.BaseRef);
    }

    // ===== 一覧と差分 =====

    [Fact]
    public async Task 分岐点基準はこのブランチで入れた変更だけを出し相手の変更は出さない()
    {
        await CommitAsync("shared.txt", "base", "first");
        await MustRunAsync("checkout", "-b", "feature");
        await CommitAsync("feature-only.txt", "mine", "feature work");
        // main 側だけが進む（分岐点基準なら、この変更は「自分が入れた変更」ではないので出ない）。
        await MustRunAsync("checkout", "main");
        await CommitAsync("main-only.txt", "theirs", "main work");
        await MustRunAsync("checkout", "feature");

        var mergeBase = await _git.ResolveCompareBaseAsync(
            new GitCompareBaseSelection(GitCompareBaseKind.MergeBase, "main"));
        var fromMergeBase = await _git.GetCompareChangesAsync(mergeBase.BaseRef!);

        Assert.Null(mergeBase.Error);
        Assert.Null(fromMergeBase.Error);
        Assert.Equal(new[] { "feature-only.txt" }, fromMergeBase.Files.Select(c => c.Path).ToArray());

        // ブランチ基準（main そのもの）だと、main が入れた変更も「消えている」ものとして出る。
        var branch = await _git.ResolveCompareBaseAsync(
            new GitCompareBaseSelection(GitCompareBaseKind.Branch, "main"));
        var fromBranch = await _git.GetCompareChangesAsync(branch.BaseRef!);

        Assert.Contains(fromBranch.Files, c => c.Path == "feature-only.txt" && c.Status == 'A');
        Assert.Contains(fromBranch.Files, c => c.Path == "main-only.txt" && c.Status == 'D');
    }

    [Fact]
    public async Task 二点記法なので未コミットの編集も一覧と差分に含まれる()
    {
        await CommitAsync("shared.txt", "base\n", "first");
        await MustRunAsync("checkout", "-b", "feature");
        // コミットしていない編集。三点記法（main...HEAD）だとここが落ちる。
        await File.WriteAllTextAsync(Path.Combine(_root, "shared.txt"), "base\nedited\n");

        var resolution = await _git.ResolveCompareBaseAsync(
            new GitCompareBaseSelection(GitCompareBaseKind.MergeBase, "main"));
        var changes = await _git.GetCompareChangesAsync(resolution.BaseRef!);

        var change = Assert.Single(changes.Files);
        Assert.Equal("shared.txt", change.Path);
        var diff = await _git.GetCompareFileDiffAsync(resolution.BaseRef!, change);
        Assert.Contains("+edited", diff);
    }

    [Fact]
    public async Task 未追跡ファイルは基準比較の一覧に出ない()
    {
        await CommitAsync("a.txt", "a", "first");
        await File.WriteAllTextAsync(Path.Combine(_root, "untracked.txt"), "new");

        var resolution = await _git.ResolveCompareBaseAsync(
            new GitCompareBaseSelection(GitCompareBaseKind.Branch, "main"));
        var changes = await _git.GetCompareChangesAsync(resolution.BaseRef!);

        // git に足していないファイルは「このブランチが基準に対して入れた変更」ではない。
        Assert.DoesNotContain(changes.Files, c => c.Path == "untracked.txt");
        // 作業ツリー基準の一覧（git status）には従来どおり未追跡として出る。
        var status = await _git.GetStatusAsync();
        Assert.Contains(status.Unstaged, e => e.Path == "untracked.txt" && e.IsUntracked);
    }

    [Fact]
    public async Task リネームは旧パス付きの1件になり差分も引ける()
    {
        await CommitAsync("docs/古い 名前.md", "内容がそこそこある行\n二行目\n三行目\n", "first");
        await MustRunAsync("checkout", "-b", "feature");
        await MustRunAsync("mv", "docs/古い 名前.md", "docs/新しい 名前.md");
        await MustRunAsync("commit", "-m", "rename");

        var resolution = await _git.ResolveCompareBaseAsync(
            new GitCompareBaseSelection(GitCompareBaseKind.MergeBase, "main"));
        var change = Assert.Single((await _git.GetCompareChangesAsync(resolution.BaseRef!)).Files);

        Assert.Equal('R', change.Status);
        Assert.Equal("docs/新しい 名前.md", change.Path);
        Assert.Equal("docs/古い 名前.md", change.OrigPath);

        var diff = await _git.GetCompareFileDiffAsync(resolution.BaseRef!, change);
        Assert.Contains("古い 名前.md", diff);
        Assert.Contains("新しい 名前.md", diff);
    }

    [Fact]
    public async Task ブランチ名と同名のディレクトリがあっても一覧が取れる()
    {
        // git は「revision かパスか」が曖昧な引数を拒む（fatal: ambiguous argument）。
        // 一覧の引数の末尾に -- が無いと、差分があるのに空＝「変更なし」と嘘をつく。
        await CommitAsync("docs/a.md", "base\n", "first");
        await MustRunAsync("branch", "docs");
        await MustRunAsync("checkout", "-b", "feature");
        await CommitAsync("docs/a.md", "base\nedited\n", "edit");

        var resolution = await _git.ResolveCompareBaseAsync(
            new GitCompareBaseSelection(GitCompareBaseKind.Branch, "docs"));
        var changes = await _git.GetCompareChangesAsync(resolution.BaseRef!);

        Assert.Null(changes.Error);
        var change = Assert.Single(changes.Files);
        Assert.Equal("docs/a.md", change.Path);
        Assert.Contains("+edited", await _git.GetCompareFileDiffAsync(resolution.BaseRef!, change));
    }

    [Fact]
    public async Task 一覧の取得が失敗したら空ではなく理由を返す()
    {
        await CommitAsync("a.txt", "a\n", "first");

        // 解決を通さずに壊れた ref を直接渡した場合でも、黙って「変更なし」にはしない。
        var changes = await _git.GetCompareChangesAsync("この参照は存在しない");

        Assert.True(changes.HasError);
        Assert.Empty(changes.Files);
    }

    [Fact]
    public async Task 同じ短縮名のrefは一覧に重複させない()
    {
        await CommitAsync("a.txt", "a\n", "first");
        // ローカルに origin/main という名前のブランチを作ると、リモート追跡側と短縮名が衝突し得る。
        await MustRunAsync("branch", "origin/main");

        var refs = await _git.GetComparableRefsAsync();

        Assert.Equal(refs.Distinct().Count(), refs.Count);
    }

    [Fact]
    public async Task 基準ブランチのラベルは何と比べているかを名乗る()
    {
        await CommitAsync("a.txt", "a", "first");

        var branch = await _git.ResolveCompareBaseAsync(
            new GitCompareBaseSelection(GitCompareBaseKind.Branch, "main"));
        var mergeBase = await _git.ResolveCompareBaseAsync(
            new GitCompareBaseSelection(GitCompareBaseKind.MergeBase, "main"));

        Assert.Contains("main", branch.Label);
        Assert.Contains("分岐点", mergeBase.Label);
    }

    // ===== ヘルパー =====

    private async Task<string> CommitAsync(string path, string content, string message)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);
        await MustRunAsync("add", "-A");
        await MustRunAsync("commit", "-m", message);
        return (await MustRunAsync("rev-parse", "HEAD")).Output.Trim();
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, $"git {string.Join(' ', args)}: {result.Message}");
        return result;
    }
}
