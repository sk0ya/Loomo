using sk0ya.Loomo.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>unified diff のハンク分解（GitPatchSplitter）の検証。</summary>
public class GitPatchSplitterTests
{
    private const string Patch =
        "diff --git a/foo.txt b/foo.txt\n" +
        "index 111..222 100644\n" +
        "--- a/foo.txt\n" +
        "+++ b/foo.txt\n" +
        "@@ -1,3 +1,3 @@\n" +
        " a\n" +
        "-b\n" +
        "+B\n" +
        " c\n" +
        "@@ -10,2 +10,3 @@\n" +
        " x\n" +
        "+y\n" +
        " z\n";

    [Fact]
    public void Split_separates_header_and_hunks()
    {
        var split = GitPatchSplitter.Split(Patch);

        Assert.Equal(2, split.Hunks.Count);
        Assert.Contains("diff --git a/foo.txt b/foo.txt", split.Header);
        Assert.Contains("+++ b/foo.txt", split.Header);
        Assert.DoesNotContain("@@", split.Header);

        Assert.StartsWith("@@ -1,3 +1,3 @@", split.Hunks[0].HeaderLine);
        Assert.StartsWith("@@ -10,2 +10,3 @@", split.Hunks[1].HeaderLine);
        Assert.EndsWith("\n", split.Hunks[0].Text);
    }

    [Fact]
    public void BuildSingleHunkPatch_combines_header_with_one_hunk()
    {
        var split = GitPatchSplitter.Split(Patch);
        var patch = GitPatchSplitter.BuildSingleHunkPatch(split.Header, split.Hunks[1]);

        Assert.Contains("--- a/foo.txt", patch);
        Assert.Contains("@@ -10,2 +10,3 @@", patch);
        Assert.Contains("+y", patch);
        // 別ハンクは含めない。
        Assert.DoesNotContain("@@ -1,3 +1,3 @@", patch);
        Assert.EndsWith("\n", patch);
    }

    [Fact]
    public void Split_with_no_hunks_returns_empty()
    {
        var split = GitPatchSplitter.Split("# untracked\nsome text\n");
        Assert.Empty(split.Hunks);
    }
}
