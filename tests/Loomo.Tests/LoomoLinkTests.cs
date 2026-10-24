using System.IO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Files;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>部屋の中の場所へのリンク <c>loomo://</c>（§24.59）。</summary>
public sealed class LoomoLinkTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Build_and_parse_round_trip_with_japanese_spaces_and_plus()
    {
        var url = LoomoLink.Build(@"C:\work\部屋 a", @"C:\work\部屋 a\src\a+b.cs", 42, 7);
        Assert.StartsWith("loomo://open?folder=", url);
        Assert.DoesNotContain(" ", url);

        Assert.True(LoomoLink.TryParse(url, out var target));
        Assert.Equal(@"C:\work\部屋 a", target.Folder);
        Assert.Equal(@"C:\work\部屋 a\src\a+b.cs", target.File);
        Assert.Equal(42, target.Line);
        Assert.Equal(7, target.Column);
    }

    [Fact]
    public void Line_and_column_are_optional()
    {
        var url = LoomoLink.Build(null, @"C:\w\a.cs");
        Assert.Equal("loomo://open?file=C%3A%5Cw%5Ca.cs", url);
        Assert.True(LoomoLink.TryParse(url, out var target));
        Assert.Null(target.Folder);
        Assert.Equal(0, target.Line);
    }

    [Theory]
    [InlineData("https://example.com/?file=C%3A%5Ca.cs")]   // 別のスキーム
    [InlineData("loomo://run?file=C%3A%5Ca.cs")]             // open 以外は読まない（走らせるリンクは作らない）
    [InlineData("loomo://open?folder=C%3A%5Cw")]             // ファイルが無い
    [InlineData("loomo://open?file=relative%5Ca.cs")]        // 相対パス
    [InlineData("")]
    public void Unusable_links_are_rejected(string text)
        => Assert.False(LoomoLink.TryParse(text, out _));

    [Fact]
    public void Startup_argument_reads_the_link_as_a_location_not_a_workspace()
    {
        var folder = Path.Combine(Path.GetTempPath(), "loomo-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "a.cs");
            var request = StartupArguments.Parse([LoomoLink.Build(folder, file, 3, 1)]);

            Assert.Null(request.WorkspaceFolder);   // 部屋を立てるかは振り分けが決める
            var location = Assert.Single(request.AtLines);
            Assert.Equal(file, location.File);
            Assert.Equal(3, location.Line);
            Assert.Equal(folder, location.Folder);
            Assert.True(request.HasItems);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void A_location_goes_to_the_room_that_contains_it()
    {
        var owner = new RelayInstance(10, [@"C:\work\app"], T0);
        var recent = new RelayInstance(20, [@"C:\work\other"], T0.AddMinutes(5));
        var request = new ExternalOpenRequest(null, [], [], [new(@"C:\work\app\a.cs", 5, 1, @"C:\work\app")]);

        var (pid, part) = Assert.Single(ExternalOpenRouting.Route(request, [owner, recent]));
        Assert.Equal(10, pid);
        Assert.Equal(5, Assert.Single(part.AtLines).Line);
        Assert.Null(part.WorkspaceFolder);
    }

    [Fact]
    public void Without_a_room_the_link_folder_is_opened_here()
    {
        var recent = new RelayInstance(20, [@"C:\work\other"], T0);
        var request = new ExternalOpenRequest(null, [], [], [new(@"C:\work\app\a.cs", 5, 1, @"C:\work\app")]);

        var (pid, part) = Assert.Single(ExternalOpenRouting.Route(request, [recent]));
        Assert.Null(pid);                                   // 自分が開く
        Assert.Equal(@"C:\work\app", part.WorkspaceFolder); // その部屋を立てて
        Assert.Single(part.AtLines);                        // そこで開く
    }

    [Fact]
    public void Without_a_room_or_folder_it_goes_to_the_most_recent_room()
    {
        var older = new RelayInstance(10, [@"C:\a"], T0);
        var recent = new RelayInstance(20, [@"C:\b"], T0.AddMinutes(5));
        var request = new ExternalOpenRequest(null, [], [], [new(@"C:\elsewhere\x.txt", 1, 0)]);

        var (pid, _) = Assert.Single(ExternalOpenRouting.Route(request, [older, recent]));
        Assert.Equal(20, pid);
    }

    [Fact]
    public void Copied_link_names_the_owning_folder_and_shows_the_display_path()
    {
        var workspace = new FakeWorkspaceService(@"C:\work\app");
        var (url, markdown, html) = LoomoLinkClipboard.Format(workspace, @"C:\work\app\src\a.cs", 12, 3);

        Assert.True(LoomoLink.TryParse(url, out var target));
        Assert.Equal(@"C:\work\app", target.Folder);
        Assert.StartsWith("[src/a.cs:12](loomo://open?", markdown);
        Assert.Contains(">src/a.cs:12</a>", html);
    }
}
