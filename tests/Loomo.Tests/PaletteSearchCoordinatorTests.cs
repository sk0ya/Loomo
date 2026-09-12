using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Abstractions;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>パレットの検索段取り（待ち・キャンセル・供給元の振り分け）の検証。</summary>
public class PaletteSearchCoordinatorTests
{
    private static Action Noop(PaletteTarget _) => () => { };

    /// <summary>呼ばれた回数と、返すものを差し替えられる検索サービス。</summary>
    private sealed class FakeSearch : IWorkspaceSearchService
    {
        public int FindCalls;
        public int GrepCalls;
        public TimeSpan Latency = TimeSpan.Zero;
        public List<FileSearchHit> Files = new();
        public List<ContentSearchHit> Contents = new();
        public Exception? Throw;

        public async Task<IReadOnlyList<FileSearchHit>> FindFilesAsync(
            string query, int max, CancellationToken ct, string? searchRoot = null)
        {
            FindCalls++;
            if (Throw is not null) throw Throw;
            await Task.Delay(Latency, ct);
            return Files;
        }

        public async Task<IReadOnlyList<ContentSearchHit>> GrepAsync(
            string query, GrepOptions options, CancellationToken ct, string? searchRoot = null)
        {
            GrepCalls++;
            await Task.Delay(Latency, ct);
            return Contents;
        }

        public Task<IReadOnlyList<AdvancedFileSearchHit>> SearchFilesAsync(
            AdvancedSearchOptions options, CancellationToken ct, string? searchRoot = null)
            => Task.FromResult<IReadOnlyList<AdvancedFileSearchHit>>(Array.Empty<AdvancedFileSearchHit>());
    }

    private static PaletteSearchCoordinator Create(
        FakeSearch search,
        Func<string, CancellationToken, Task<IReadOnlyList<PaletteLocation>>>? symbols = null,
        int delayMs = 1)
        => new(search,
            symbols ?? ((_, _) => Task.FromResult<IReadOnlyList<PaletteLocation>>(Array.Empty<PaletteLocation>())),
            searchDelayMs: delayMs, previewDelayMs: delayMs);

    [Fact]
    public async Task File_mode_maps_hits_and_counts_them()
    {
        var search = new FakeSearch
        {
            Files = { new FileSearchHit(@"C:\w\src\Shell.cs", "src/Shell.cs", 0) },
        };
        var status = new List<string>();

        var outcome = await Create(search).SearchAsync(PaletteQuery.Parse("/shell"), Noop, status.Add);

        Assert.NotNull(outcome);
        Assert.Equal("1 件", outcome!.Status);
        Assert.Equal("Shell.cs", outcome.Items[0].Title);
        // 待ちに入る前に「検索中…」を同期で返すので、UI は空白のまま固まらない。
        Assert.Equal(new[] { "検索中…" }, status);
    }

    [Fact]
    public async Task Empty_file_query_still_searches()
    {
        // ファイル名だけは空クエリに意味がある（一覧代わりに全件の先頭が出る）。
        var search = new FakeSearch();
        var outcome = await Create(search).SearchAsync(PaletteQuery.Parse("/"), Noop, _ => { });

        Assert.Equal(1, search.FindCalls);
        Assert.Equal("一致なし", outcome!.Status);
    }

    [Fact]
    public async Task Text_search_waits_for_enough_characters()
    {
        var search = new FakeSearch();

        var outcome = await Create(search).SearchAsync(PaletteQuery.Parse("#a"), Noop, _ => { });

        Assert.Equal(0, search.GrepCalls);   // 1文字では部屋中を総なめにしない
        Assert.Empty(outcome!.Items);
        Assert.Contains("2 文字以上", outcome.Status);
    }

    [Fact]
    public async Task Text_mode_carries_the_term_for_highlighting()
    {
        var search = new FakeSearch
        {
            Contents = { new ContentSearchHit(@"C:\w\a.cs", "a.cs", 3, 1, "  var todo = 1;") },
        };

        var outcome = await Create(search).SearchAsync(PaletteQuery.Parse("#todo"), Noop, _ => { });

        Assert.Equal(1, search.GrepCalls);
        Assert.Equal("todo", outcome!.Items[0].Target!.Highlight);
        Assert.Equal(3, outcome.Items[0].Target!.Line);
    }

    [Fact]
    public async Task A_superseded_search_returns_null_so_it_cannot_overwrite_the_newer_one()
    {
        var search = new FakeSearch { Latency = TimeSpan.FromMilliseconds(200) };
        var coordinator = Create(search);

        var first = coordinator.SearchAsync(PaletteQuery.Parse("/aaa"), Noop, _ => { });
        await Task.Delay(30);
        search.Latency = TimeSpan.Zero;
        search.Files = new List<FileSearchHit> { new(@"C:\w\b.cs", "b.cs", 0) };
        var second = await coordinator.SearchAsync(PaletteQuery.Parse("/bbb"), Noop, _ => { });

        Assert.Null(await first);
        Assert.Equal("b.cs", second!.Items[0].Title);
    }

    [Fact]
    public async Task Cancel_stops_the_pending_search()
    {
        var search = new FakeSearch { Latency = TimeSpan.FromMilliseconds(200) };
        var coordinator = Create(search);

        var pending = coordinator.SearchAsync(PaletteQuery.Parse("/aaa"), Noop, _ => { });
        coordinator.Cancel();

        Assert.Null(await pending);
    }

    [Fact]
    public async Task Symbol_mode_says_the_language_server_may_be_missing_when_nothing_matched()
    {
        var outcome = await Create(new FakeSearch()).SearchAsync(PaletteQuery.Parse("@Foo"), Noop, _ => { });

        Assert.Empty(outcome!.Items);
        Assert.Contains("言語サーバー", outcome.Status);
    }

    [Fact]
    public async Task Symbol_mode_uses_the_supplied_provider()
    {
        var coordinator = Create(new FakeSearch(), (query, _) =>
            Task.FromResult<IReadOnlyList<PaletteLocation>>(
                new[] { new PaletteLocation(@"C:\w\a.cs", "a.cs", 7, 2, query, "Ns") }));

        var outcome = await coordinator.SearchAsync(PaletteQuery.Parse("@Filter"), Noop, _ => { });

        Assert.Equal("Filter", outcome!.Items[0].Title);
        Assert.Equal("a.cs:7", outcome.Items[0].Category);
    }

    [Fact]
    public async Task A_failing_search_is_reported_instead_of_crashing()
    {
        var search = new FakeSearch { Throw = new InvalidOperationException("rg が壊れた") };

        var outcome = await Create(search).SearchAsync(PaletteQuery.Parse("/x"), Noop, _ => { });

        Assert.Empty(outcome!.Items);
        Assert.Contains("rg が壊れた", outcome.Status);
    }

    [Fact]
    public async Task Command_mode_is_not_this_coordinators_job()
        => Assert.Null(await Create(new FakeSearch()).SearchAsync(PaletteQuery.Parse("エディタ"), Noop, _ => { }));

    [Fact]
    public async Task Preview_reads_the_selected_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-palette-{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(path, new[] { "one", "two", "three" });
        try
        {
            var content = await Create(new FakeSearch()).PreviewAsync(new PaletteTarget(path, 2), "a.txt");

            Assert.NotNull(content);
            Assert.Equal("two", content!.Lines.Single(l => l.IsTarget).Text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_superseded_preview_returns_null()
    {
        var coordinator = Create(new FakeSearch(), delayMs: 200);
        var missing = Path.Combine(Path.GetTempPath(), "loomo-palette-none.txt");

        var first = coordinator.PreviewAsync(new PaletteTarget(missing), "none.txt");
        coordinator.CancelPreview();

        Assert.Null(await first);
    }
}
