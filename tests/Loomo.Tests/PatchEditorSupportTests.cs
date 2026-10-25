using System.Linq;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Diff;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>§24.68 パッチ（.patch／.diff）を EditorSupport で差分として読む。</summary>
public sealed class PatchEditorSupportTests
{
    private const string GitPatch = """
        From 1234567 Mon Sep 17 00:00:00 2001
        Subject: [PATCH] 名前を直す

        ---
         src/a.cs | 3 ++-
         1 file changed

        diff --git a/src/a.cs b/src/a.cs
        index 1111111..2222222 100644
        --- a/src/a.cs
        +++ b/src/a.cs
        @@ -10,4 +10,4 @@ class A
         {
        -    ClearDiagnostics(e);
        +    ClearDiagnostic(e);
             Done();
         }
        diff --git a/new.txt b/new.txt
        new file mode 100644
        index 0000000..3333333
        --- /dev/null
        +++ b/new.txt
        @@ -0,0 +1,2 @@
        +hello
        +world
        diff --git a/old name.txt b/new name.txt
        similarity index 100%
        rename from old name.txt
        rename to new name.txt
        diff --git a/img.png b/img.png
        index 4444444..5555555 100644
        Binary files a/img.png and b/img.png differ
        --
        2.47.0

        """;

    [Fact]
    public void Splits_files_and_reads_paths_kinds_and_counts()
    {
        var patch = PatchDocument.Parse(GitPatch);

        Assert.Equal(4, patch.Files.Count);
        var a = patch.Files[0];
        Assert.Equal(("src/a.cs", "src/a.cs"), (a.OldPath, a.NewPath));
        Assert.Equal((1, 1), (a.Added, a.Removed));
        Assert.Null(patch.Files[1].OldPath);
        Assert.Equal("new.txt", patch.Files[1].NewPath);
        Assert.Equal("old name.txt → new name.txt", patch.Files[2].DisplayPath);
        Assert.True(patch.Files[3].IsBinary);
        Assert.StartsWith("From 1234567", patch.Preamble[0]);
    }

    [Fact]
    public void Numbers_lines_from_the_hunk_header()
    {
        var lines = PatchDocument.Parse(GitPatch).Files[0].Hunks.Single().Lines;

        Assert.Equal(new int?[] { 10, 11, null, 12, 13 }, lines.Select(l => l.OldLine));
        Assert.Equal(new int?[] { 10, null, 11, 12, 13 }, lines.Select(l => l.NewLine));
        Assert.Equal("    ClearDiagnostic(e);", lines[2].Text);
    }

    [Fact]
    public void Reads_a_plain_diff_u_with_timestamps()
    {
        var patch = PatchDocument.Parse("--- a.txt\t2026-10-25 10:00:00\n+++ b.txt\t2026-10-25 10:01:00\n@@ -1 +1 @@\n-x\n+y\n");
        var file = Assert.Single(patch.Files);
        Assert.Equal(("a.txt", "b.txt"), (file.OldPath, file.NewPath));
        Assert.Equal((1, 1), (file.Added, file.Removed));
    }

    [Fact]
    public void Reads_a_bare_hunk_without_file_headers()
    {
        var file = Assert.Single(PatchDocument.Parse("@@ -3,2 +3,2 @@\n a\n-b\n+c\n").Files);
        Assert.Null(file.NewPath);
        Assert.Equal(3, file.Hunks[0].Lines.Count);
    }

    [Fact]
    public void Text_that_is_not_a_patch_says_so()
    {
        var html = PatchRenderer.Render(PatchDocument.Parse("ただのメモ\n"));
        Assert.Contains("差分が見つかりません", html);
    }

    [Fact]
    public void Renders_line_numbers_signs_and_only_the_changed_part()
    {
        var html = PatchRenderer.Render(PatchDocument.Parse(GitPatch));

        Assert.Contains("4 ファイル", html);
        Assert.Contains("<span class=\"tag\">追加</span>", html);
        Assert.Contains("<span class=\"tag\">名前の変更</span>", html);
        Assert.Contains("<span class=\"tag\">バイナリ</span>", html);
        // ClearDiagnostics → ClearDiagnostic は「s」だけが変わった範囲
        Assert.Contains("ClearDiagnostic<span class=\"chg\">s</span>(e);", html);
        Assert.Contains("<tr class=\"add\"><td class=\"ln\"></td><td class=\"ln\">11</td>", html);
        Assert.Contains("href=\"#f1\"", html);   // 複数ファイルなら目次
    }

    [Fact]
    public void Escapes_html_in_the_patch()
    {
        var html = PatchRenderer.Render(PatchDocument.Parse("@@ -1 +1 @@\n-<b>\n+<i>\n"));
        Assert.DoesNotContain("<b>", html);
        Assert.DoesNotContain("<i>", html);
        Assert.Contains("&lt;<span class=\"chg\">i</span>&gt;", html);
    }

    [Fact]
    public void Registry_picks_the_patch_provider_for_patch_and_diff()
    {
        var registry = new EditorSupportRegistry([new PatchEditorSupport(new LoomoSettings())]);
        Assert.IsType<PatchEditorSupport>(registry.Resolve(@"C:\work\fix.patch"));
        Assert.IsType<PatchEditorSupport>(registry.Resolve(@"C:\work\FIX.DIFF"));
        Assert.Equal("Patch: fix.patch", new PatchEditorSupport(new LoomoSettings()).DescribeTitle(@"C:\work\fix.patch"));
    }
}
