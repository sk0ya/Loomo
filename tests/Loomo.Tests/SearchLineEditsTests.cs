using System.IO;
using System.Text;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Tests;

/// <summary>検索結果をその場で書き換えて元のファイルへ戻す（§24.86）。</summary>
public sealed class SearchLineEditsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"loomo-search-edit-{Guid.NewGuid():N}");

    public SearchLineEditsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void 行ごとに違う書き換えを当て_改行コードと行末の空白を保つ()
    {
        var (text, applied, stale) = SearchLineEdits.Apply("Call(a);  \r\nkeep\r\nCall(b);\r\n",
            [new SearchLineEdit(1, "Call(a);", "Call(a, 1);"), new SearchLineEdit(3, "Call(b);", "Call(b, 2);")]);

        Assert.Equal("Call(a, 1);  \r\nkeep\r\nCall(b, 2);\r\n", text);
        Assert.Equal((2, 0), (applied, stale));
    }

    [Fact]
    public void 検索した後に変わった行は飛ばす()
    {
        var (text, applied, stale) = SearchLineEdits.Apply("Call(x);\nend",
            [new SearchLineEdit(1, "Call(a);", "Call(a, 1);"), new SearchLineEdit(9, "gone", "x")]);

        Assert.Equal("Call(x);\nend", text);
        Assert.Equal((0, 2), (applied, stale));
    }

    [Fact]
    public void ファイルの文字コードとBOMのまま書く()
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "名前 = 1;\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = SearchLineEdits.ApplyToFile(path, [new SearchLineEdit(1, "名前 = 1;", "名前 = 2;")]);

        Assert.Equal(new SearchLineEditResult(1, 0), result);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("名前 = 2;\n", new UTF8Encoding(false).GetString(bytes[3..]));
    }

    [Fact]
    public void 同じ行の一致は一欄にまとめ_書き戻すと書き換えた行だけが変わる()
    {
        var a = Path.Combine(_root, "a.cs");
        var b = Path.Combine(_root, "b.cs");
        File.WriteAllText(a, "Log(x); Log(y);\nLog(z);\n");
        File.WriteAllText(b, "Log(w);\n");
        var sut = CreateSut();
        Show(sut, "Log",
            new ContentSearchHit(a, "a.cs", 1, 1, "Log(x); Log(y);"),
            new ContentSearchHit(a, "a.cs", 1, 9, "Log(x); Log(y);"),
            new ContentSearchHit(a, "a.cs", 2, 1, "Log(z);"),
            new ContentSearchHit(b, "b.cs", 1, 1, "Log(w);"));
        var written = new List<string>();
        sut.FilesReplacedOnDisk += (_, paths) => written.AddRange(paths);

        sut.BeginEditLinesCommand.Execute(null);

        Assert.True(sut.IsEditingLines);
        Assert.Equal(3, sut.LineEdits.Count);
        Assert.Equal([true, false, true], sut.LineEdits.Select(l => l.IsFirstOfFile));
        sut.LineEdits[1].Edited = "Log(z, 1);";
        Assert.Equal(1, sut.ChangedLineCount);

        var (lines, files, skipped) = sut.ApplyLineEdits();

        Assert.Equal((1, 1, 0), (lines, files, skipped));
        Assert.Equal("Log(x); Log(y);\nLog(z, 1);\n", File.ReadAllText(a));
        Assert.Equal("Log(w);\n", File.ReadAllText(b));
        Assert.Equal([a], written);
        Assert.False(sut.IsEditingLines);
    }

    [Fact]
    public void 未保存のタブのファイルには書かない()
    {
        var a = Path.Combine(_root, "a.cs");
        File.WriteAllText(a, "Log(z);\n");
        var sut = CreateSut();
        sut.HasUnsavedEdits = path => path == a;
        Show(sut, "Log", new ContentSearchHit(a, "a.cs", 1, 1, "Log(z);"));
        sut.BeginEditLinesCommand.Execute(null);
        sut.LineEdits[0].Edited = "Log(z, 1);";

        var (lines, _, skipped) = sut.ApplyLineEdits();

        Assert.Equal((0, 1), (lines, skipped));
        Assert.Equal("Log(z);\n", File.ReadAllText(a));
        Assert.Contains("未保存", sut.StatusMessage);
    }

    [Fact]
    public void 改行は入れられない()
    {
        var item = new SearchLineEditItem("p", "p", 1, "a", isFirstOfFile: true) { Edited = "b\r\nc" };

        Assert.Equal("bc", item.Edited);
    }

    private SearchPanelViewModel CreateSut()
    {
        var mapper = new SearchResultTreeMapper();
        return new SearchPanelViewModel(new FakeWorkspaceService(_root), new SearchPanelQuery(new NoSearch(), mapper), mapper);
    }

    private static void Show(SearchPanelViewModel sut, string query, params ContentSearchHit[] hits)
    {
        sut.Query = query;
        sut.CancelSearchCommand.Execute(null);
        sut.Results.Clear();
        var groups = hits.GroupBy(h => h.FullPath)
            .Select(g => new SearchFileGroup(g.Key, g.First().RelativePath, g.Select(h => new SearchMatchItem(h))))
            .ToList();
        foreach (var node in new SearchResultTreeMapper().Map(groups))
            sut.Results.Add(node);
    }

    private sealed class NoSearch : IWorkspaceSearchService
    {
        public Task<IReadOnlyList<FileSearchHit>> FindFilesAsync(string query, int max, CancellationToken ct, string? searchRoot = null)
            => Task.FromResult<IReadOnlyList<FileSearchHit>>(Array.Empty<FileSearchHit>());
        public Task<IReadOnlyList<ContentSearchHit>> GrepAsync(string query, GrepOptions options, CancellationToken ct, string? searchRoot = null)
            => Task.FromResult<IReadOnlyList<ContentSearchHit>>(Array.Empty<ContentSearchHit>());
        public Task<IReadOnlyList<AdvancedFileSearchHit>> SearchFilesAsync(AdvancedSearchOptions options, CancellationToken ct, string? searchRoot = null)
            => Task.FromResult<IReadOnlyList<AdvancedFileSearchHit>>(Array.Empty<AdvancedFileSearchHit>());
    }
}
