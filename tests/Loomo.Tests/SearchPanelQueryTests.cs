using System.IO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Tests;

public sealed class SearchPanelQueryTests : IDisposable
{
    private readonly string _dir;
    private readonly SearchPanelQuery _sut;

    public SearchPanelQueryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "loomo-search-query-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _sut = new SearchPanelQuery(new NotUsedSearchService(), new SearchResultTreeMapper());
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ReplaceOneInFile_replaces_only_the_match_at_the_given_position()
    {
        var path = WriteFile("a.txt", "foo bar\nfoo baz\n");

        var ok = _sut.ReplaceOneInFile(path, "foo", "qux", caseSensitive: false, useRegex: false, line: 1, column: 1);

        Assert.True(ok);
        Assert.Equal("qux bar\nfoo baz\n", File.ReadAllText(path));
    }

    [Fact]
    public void ReplaceOneInFile_disambiguates_multiple_matches_on_the_same_line_by_column()
    {
        var path = WriteFile("a.txt", "foo foo\n");

        var ok = _sut.ReplaceOneInFile(path, "foo", "qux", caseSensitive: false, useRegex: false, line: 1, column: 5);

        Assert.True(ok);
        Assert.Equal("foo qux\n", File.ReadAllText(path));
    }

    [Fact]
    public void ReplaceOneInFile_returns_false_when_position_no_longer_matches()
    {
        var path = WriteFile("a.txt", "foo bar\n");
        var original = File.ReadAllText(path);

        var ok = _sut.ReplaceOneInFile(path, "foo", "qux", caseSensitive: false, useRegex: false, line: 1, column: 5);

        Assert.False(ok);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void ReplaceOneInFile_returns_false_for_missing_line()
    {
        var path = WriteFile("a.txt", "foo bar\n");

        var ok = _sut.ReplaceOneInFile(path, "foo", "qux", caseSensitive: false, useRegex: false, line: 5, column: 1);

        Assert.False(ok);
    }

    [Fact]
    public void ReplaceOneInFile_supports_regex_backreferences_for_the_matched_occurrence_only()
    {
        var path = WriteFile("a.txt", "id(1) id(2)\n");

        var ok = _sut.ReplaceOneInFile(path, @"id\((\d+)\)", "value[$1]", caseSensitive: false, useRegex: true, line: 1, column: 7);

        Assert.True(ok);
        Assert.Equal("id(1) value[2]\n", File.ReadAllText(path));
    }

    [Fact]
    public void ReplaceOneInFile_handles_crlf_line_endings()
    {
        var path = WriteFile("a.txt", "foo bar\r\nfoo baz\r\n");

        var ok = _sut.ReplaceOneInFile(path, "foo", "qux", caseSensitive: false, useRegex: false, line: 2, column: 1);

        Assert.True(ok);
        Assert.Equal("foo bar\r\nqux baz\r\n", File.ReadAllText(path));
    }

    private sealed class NotUsedSearchService : IWorkspaceSearchService
    {
        public Task<IReadOnlyList<FileSearchHit>> FindFilesAsync(string query, int max, CancellationToken ct, string? searchRoot = null)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<ContentSearchHit>> GrepAsync(string query, GrepOptions options, CancellationToken ct, string? searchRoot = null)
            => throw new NotSupportedException();
    }
}
