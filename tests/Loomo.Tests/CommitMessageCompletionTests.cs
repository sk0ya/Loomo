using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>コミットメッセージの補完の候補（§24.95）。</summary>
public sealed class CommitMessageCompletionTests
{
    private const string Diff = """
        diff --git a/src/GitPanelViewModel.cs b/src/GitPanelViewModel.cs
        --- a/src/GitPanelViewModel.cs
        +++ b/src/GitPanelViewModel.cs
        @@ -10,0 +11,3 @@ public sealed class GitPanelViewModel
        +    public async Task RefreshCommitBuildsAsync(string root)
        +    {
        +        var builds = await LoadBuildsAsync(root);
        @@ -40 +43 @@ private void UpdateSummary()
        -        _panel.FetchSummary = null;
        +        _panel.FetchSummary = Describe(builds);
        """;

    [Fact]
    public void 変えたファイルの名前と_変えた行の識別子を候補にし_キーワードは入れない()
    {
        var candidates = CommitMessageCompletion.Build(Diff, ["src/GitPanelViewModel.cs"]);

        Assert.Contains("GitPanelViewModel.cs", candidates);
        Assert.Contains("GitPanelViewModel", candidates);
        Assert.Contains("RefreshCommitBuildsAsync", candidates);
        Assert.Contains("LoadBuildsAsync", candidates);
        Assert.Contains("UpdateSummary", candidates);   // hunk の見出しの囲み
        Assert.DoesNotContain("public", candidates);
        Assert.DoesNotContain("await", candidates);
        Assert.DoesNotContain("var", candidates);
        // ファイル名が先頭、次に hunk の囲み、そのあと変えた行に多く出た語。
        Assert.Equal("GitPanelViewModel", candidates[0]);
        Assert.True(candidates.ToList().IndexOf("UpdateSummary") < candidates.ToList().IndexOf("LoadBuildsAsync"));
    }

    [Fact]
    public void 差分の見出しの行は読まない()
    {
        var candidates = CommitMessageCompletion.Build("--- a/Secret.cs\n+++ b/Secret.cs\n", []);
        Assert.DoesNotContain("Secret", candidates);
    }

    [Fact]
    public void 打ちかけの語に前方一致で合わせ_大文字小文字は区別しない()
    {
        IReadOnlyList<string> candidates = ["RefreshCommitBuildsAsync", "Refresh", "GitPanelViewModel.cs", "GitPanelViewModel"];

        var (start, prefix, matches) = CommitMessageCompletion.Suggest(candidates, "fix: refr", 9);

        Assert.Equal(5, start);
        Assert.Equal("refr", prefix);
        Assert.Equal(new[] { "RefreshCommitBuildsAsync", "Refresh" }, matches);
    }

    [Fact]
    public void ファイル名の途中でも合い_ドットの後ろだけでも合う()
    {
        IReadOnlyList<string> candidates = ["GitPanelViewModel.cs", "RefreshCommitBuildsAsync"];

        Assert.Equal(new[] { "GitPanelViewModel.cs" }, CommitMessageCompletion.Suggest(candidates, "GitPanelViewModel.c", 19).Matches);
        var (start, _, matches) = CommitMessageCompletion.Suggest(candidates, "vm.Refr", 7);
        Assert.Equal(3, start);
        Assert.Equal(new[] { "RefreshCommitBuildsAsync" }, matches);
    }

    [Fact]
    public void 一文字では出さず_打ち終えた語そのものは出さない()
    {
        IReadOnlyList<string> candidates = ["Refresh"];
        Assert.Empty(CommitMessageCompletion.Suggest(candidates, "R", 1).Matches);
        Assert.Empty(CommitMessageCompletion.Suggest(candidates, "Refresh", 7).Matches);
    }
}
