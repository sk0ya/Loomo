using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ブラウザペイン（§21）のブックマーク・履歴・アドレス正規化・ページ Markdown 化の検証。
/// UI と WebView2 を持たない純関数だけを対象にする。
/// </summary>
public class BrowserLibraryTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"loomo-browser-{Guid.NewGuid():N}.json");

    // ===== 履歴 =====

    [Fact]
    public void Same_url_visited_twice_is_one_entry_with_count()
    {
        var now = DateTime.UtcNow;
        var history = BrowserLibrary.RecordVisit(new List<BrowserHistoryEntry>(), "https://example.com/a", "A", now);
        history = BrowserLibrary.RecordVisit(history, "https://example.com/a", "A", now.AddMinutes(1));

        var entry = Assert.Single(history);
        Assert.Equal(2, entry.VisitCount);
        Assert.Equal(now.AddMinutes(1), entry.LastVisitedUtc);
    }

    [Fact]
    public void Revisit_moves_entry_to_front()
    {
        var now = DateTime.UtcNow;
        var history = BrowserLibrary.RecordVisit(new List<BrowserHistoryEntry>(), "https://a.example/", "A", now);
        history = BrowserLibrary.RecordVisit(history, "https://b.example/", "B", now.AddMinutes(1));
        history = BrowserLibrary.RecordVisit(history, "https://a.example/", "A", now.AddMinutes(2));

        Assert.Equal("https://a.example/", history[0].Url);
    }

    [Fact]
    public void Empty_title_does_not_erase_a_known_title()
    {
        // タイトルはナビゲーション完了より後に確定する。空で上書きしてしまうと見出しが消える。
        var now = DateTime.UtcNow;
        var history = BrowserLibrary.RecordVisit(new List<BrowserHistoryEntry>(), "https://example.com/", "実タイトル", now);
        history = BrowserLibrary.RecordVisit(history, "https://example.com/", null, now.AddSeconds(1));

        Assert.Equal("実タイトル", history[0].Title);
    }

    [Fact]
    public void Fragment_and_trailing_slash_are_the_same_page()
    {
        Assert.True(BrowserLibrary.SameUrl("https://example.com/docs/", "https://example.com/docs"));
        Assert.True(BrowserLibrary.SameUrl("https://example.com/docs#top", "https://example.com/docs"));
        // クエリは別ページとして残す（検索結果や絞り込みは行き先が違う）。
        Assert.False(BrowserLibrary.SameUrl("https://example.com/s?q=1", "https://example.com/s?q=2"));
    }

    [Fact]
    public void Only_http_urls_are_recorded()
    {
        Assert.True(BrowserLibrary.IsRecordable("https://example.com/"));
        Assert.False(BrowserLibrary.IsRecordable("about:blank"));
        Assert.False(BrowserLibrary.IsRecordable(""));
        Assert.False(BrowserLibrary.IsRecordable(null));
    }

    [Fact]
    public void Trim_keeps_the_most_recent_entries()
    {
        var now = DateTime.UtcNow;
        var history = Enumerable.Range(0, 10)
            .Select(i => new BrowserHistoryEntry { Url = $"https://e{i}.example/", LastVisitedUtc = now.AddMinutes(i) })
            .ToList();

        var trimmed = BrowserLibrary.Trim(history, 3);

        Assert.Equal(3, trimmed.Count);
        Assert.Equal("https://e9.example/", trimmed[0].Url);
    }

    // ===== 候補 =====

    [Fact]
    public void Suggestions_are_empty_for_empty_query()
    {
        var bookmarks = new List<BrowserBookmark> { new() { Url = "https://example.com/" } };

        Assert.Empty(BrowserLibrary.Suggest(bookmarks, new List<BrowserHistoryEntry>(), ""));
        Assert.Empty(BrowserLibrary.Suggest(bookmarks, new List<BrowserHistoryEntry>(), "   "));
    }

    [Fact]
    public void Host_prefix_match_ignores_scheme_and_www()
    {
        var history = new List<BrowserHistoryEntry> { new() { Url = "https://www.github.com/sk0ya", Title = "sk0ya" } };

        var suggestions = BrowserLibrary.Suggest(new List<BrowserBookmark>(), history, "git");

        Assert.Single(suggestions);
        Assert.Equal("https://www.github.com/sk0ya", suggestions[0].Url);
    }

    [Fact]
    public void Bookmarks_rank_above_history_and_duplicates_collapse()
    {
        var bookmarks = new List<BrowserBookmark> { new() { Url = "https://example.com/docs" } };
        var history = new List<BrowserHistoryEntry> {
            new() { Url = "https://example.com/docs/", VisitCount = 99 },   // 同じページ（末尾スラッシュ違い）
            new() { Url = "https://example.com/blog", VisitCount = 1 },
        };

        var suggestions = BrowserLibrary.Suggest(bookmarks, history, "example");

        Assert.Equal(2, suggestions.Count);
        Assert.True(suggestions[0].IsBookmark);
        Assert.Equal("https://example.com/blog", suggestions[1].Url);
    }

    [Fact]
    public void Title_match_is_weaker_than_url_match()
    {
        var history = new List<BrowserHistoryEntry> {
            new() { Url = "https://other.example/x", Title = "loomo のこと" },
            new() { Url = "https://loomo.example/", Title = "ホーム" },
        };

        var suggestions = BrowserLibrary.Suggest(new List<BrowserBookmark>(), history, "loomo");

        Assert.Equal("https://loomo.example/", suggestions[0].Url);
    }

    [Fact]
    public void Suggest_respects_the_limit()
    {
        var history = Enumerable.Range(0, 20)
            .Select(i => new BrowserHistoryEntry { Url = $"https://example.com/{i}" })
            .ToList();

        Assert.Equal(5, BrowserLibrary.Suggest(new List<BrowserBookmark>(), history, "example", limit: 5).Count);
    }

    // ===== 永続化 =====

    [Fact]
    public void Store_round_trips_and_caps_history()
    {
        var path = TempFile();
        var store = new BrowserLibraryStore(path, maxHistory: 2);
        var now = DateTime.UtcNow;
        store.Save(new BrowserLibrarySnapshot {
            Bookmarks = { new BrowserBookmark { Url = "https://pinned.example/", Title = "留め置き" } },
            History = {
                new BrowserHistoryEntry { Url = "https://old.example/", LastVisitedUtc = now.AddHours(-2) },
                new BrowserHistoryEntry { Url = "https://mid.example/", LastVisitedUtc = now.AddHours(-1) },
                new BrowserHistoryEntry { Url = "https://new.example/", LastVisitedUtc = now },
            },
        });

        var loaded = new BrowserLibraryStore(path, maxHistory: 2).Load();

        Assert.Equal("https://pinned.example/", Assert.Single(loaded.Bookmarks).Url);
        Assert.Equal(2, loaded.History.Count);
        Assert.DoesNotContain(loaded.History, e => e.Url == "https://old.example/");
        File.Delete(path);
    }

    [Fact]
    public void Broken_file_loads_as_empty_instead_of_throwing()
    {
        var path = TempFile();
        File.WriteAllText(path, "{ これは JSON ではない");

        var loaded = new BrowserLibraryStore(path).Load();

        Assert.Empty(loaded.Bookmarks);
        Assert.Empty(loaded.History);
        File.Delete(path);
    }

    [Fact]
    public void Toggling_bookmark_adds_then_removes_the_current_page()
    {
        var path = TempFile();
        var vm = new BrowserViewModel(new BrowserLibraryStore(path));
        vm.RecordVisit("https://example.com/docs", "Docs");

        vm.ToggleBookmark();
        Assert.True(vm.IsBookmarked);
        Assert.Equal("https://example.com/docs", Assert.Single(vm.Bookmarks).Url);

        vm.ToggleBookmark();
        Assert.False(vm.IsBookmarked);
        Assert.Empty(vm.Bookmarks);
        File.Delete(path);
    }

    [Fact]
    public void Switching_tabs_or_a_late_title_does_not_count_a_new_visit()
    {
        var path = TempFile();
        var vm = new BrowserViewModel(new BrowserLibraryStore(path));
        vm.RecordVisit("https://example.com/a", null);

        vm.UpdateCurrentTitle("https://example.com/a", "あとから確定したタイトル");   // DocumentTitleChanged
        vm.SetCurrentPage("https://example.com/a", "あとから確定したタイトル");       // タブ切替

        vm.IsLibraryOpen = true;   // 一覧は開いたときに作り直す
        var entry = Assert.Single(vm.History);
        Assert.Equal("あとから確定したタイトル", entry.DisplayTitle);
        Assert.Single(new BrowserLibraryStore(path).Load().History.Where(h => h.VisitCount == 1));
        File.Delete(path);
    }

    [Fact]
    public void A_ticking_page_title_does_not_rewrite_the_file_every_time()
    {
        // 未読件数や時計をタイトルに出すページは題を延々と打ち替える。表示は追随しても、
        // ファイルへの書き出しは「最初に題が付いたとき」だけに留める。
        var path = TempFile();
        var vm = new BrowserViewModel(new BrowserLibraryStore(path));
        vm.RecordVisit("https://mail.example/", null);

        vm.UpdateCurrentTitle("https://mail.example/", "(1) 受信トレイ");
        var afterFirstTitle = File.ReadAllText(path);

        vm.UpdateCurrentTitle("https://mail.example/", "(2) 受信トレイ");
        vm.UpdateCurrentTitle("https://mail.example/", "(3) 受信トレイ");

        Assert.Equal("(1) 受信トレイ",
            Assert.Single(new BrowserLibraryStore(path).Load().History).Title);
        Assert.Equal(afterFirstTitle, File.ReadAllText(path));   // 以降は書き直していない
        vm.IsLibraryOpen = true;                                  // 表示は最新の題に追いつく
        Assert.Equal("(3) 受信トレイ", Assert.Single(vm.History).DisplayTitle);
        File.Delete(path);
    }

    [Fact]
    public void Bookmark_state_follows_the_page_being_shown()
    {
        var path = TempFile();
        var vm = new BrowserViewModel(new BrowserLibraryStore(path));
        vm.RecordVisit("https://example.com/a", "A");
        vm.ToggleBookmark();

        vm.RecordVisit("https://example.com/b", "B");
        Assert.False(vm.IsBookmarked);

        // 末尾スラッシュ違いでも「同じページ」として★が点く。
        vm.RecordVisit("https://example.com/a/", "A");
        Assert.True(vm.IsBookmarked);
        File.Delete(path);
    }

    [Fact]
    public void Star_lights_up_once_the_library_is_read()
    {
        // 起動直後（復元でタブを切り替えただけ）は browser.json を読まない。読んだ時点で★が揃う。
        var path = TempFile();
        new BrowserLibraryStore(path).Save(new BrowserLibrarySnapshot {
            Bookmarks = { new BrowserBookmark { Url = "https://example.com/a" } },
        });
        var vm = new BrowserViewModel(new BrowserLibraryStore(path));

        vm.SetCurrentPage("https://example.com/a", "A");
        Assert.False(vm.IsBookmarked);

        vm.IsLibraryOpen = true;
        Assert.True(vm.IsBookmarked);
        File.Delete(path);
    }

    [Fact]
    public void Non_http_pages_cannot_be_bookmarked()
    {
        var path = TempFile();
        var vm = new BrowserViewModel(new BrowserLibraryStore(path));
        vm.RecordVisit("about:blank", null);

        vm.ToggleBookmark();

        Assert.False(vm.IsBookmarked);
        Assert.Empty(vm.Bookmarks);
    }

    // ===== アドレス欄の正規化 =====

    [Theory]
    [InlineData("example.com", "https://example.com")]
    [InlineData("localhost:5173", "http://localhost:5173")]
    [InlineData("https://example.com/x", "https://example.com/x")]
    public void Address_gets_a_scheme_when_it_looks_like_a_host(string input, string expected)
        => Assert.Equal(expected, WorkspaceSessionCoordinator.NormalizeBrowserAddress(input, "https://home/"));

    [Theory]
    [InlineData("loomo とは")]
    [InlineData("wpf")]
    public void Address_without_a_dot_becomes_a_search(string input)
        => Assert.StartsWith("https://www.google.com/search?q=",
            WorkspaceSessionCoordinator.NormalizeBrowserAddress(input, "https://home/"));

    [Theory]
    [InlineData(@"C:\notes\a.html", "file:///C:/notes/a.html")]
    [InlineData(@"\\srv\share\a.txt", "file://srv/share/a.txt")]
    public void Local_paths_become_file_urls(string input, string expected)
    {
        // ドライブレターは未知スキーム "C" に見えるので、スキーム判定だけに任せると
        // https://C:\notes\a.html という Uri に載らない文字列ができ、遷移で例外になる。
        var normalized = WorkspaceSessionCoordinator.NormalizeBrowserAddress(input, "https://home/");

        Assert.Equal(expected, normalized);
        Assert.True(Uri.TryCreate(normalized, UriKind.Absolute, out _));
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("localhost:5173")]
    [InlineData(@"C:\notes\a.html")]
    [InlineData("loomo とは")]
    [InlineData("https://example.com/x")]
    public void Normalized_address_is_always_a_usable_absolute_uri(string input)
        => Assert.True(Uri.TryCreate(
            WorkspaceSessionCoordinator.NormalizeBrowserAddress(input, "https://home/"),
            UriKind.Absolute, out _));

    [Fact]
    public void Empty_address_falls_back_to_the_default_page()
        => Assert.Equal("https://home/", WorkspaceSessionCoordinator.NormalizeBrowserAddress("  ", "https://home/"));

    // ===== ページの Markdown 化 =====

    [Fact]
    public void Page_document_carries_title_and_source_url()
    {
        var markdown = BrowserPageMarkdown.BuildDocument("記事タイトル", "https://example.com/a", "本文");

        Assert.StartsWith("# 記事タイトル", markdown);
        Assert.Contains("<https://example.com/a>", markdown);
        Assert.Contains("本文", markdown);
    }

    [Fact]
    public void Page_document_collapses_runs_of_blank_lines()
        => Assert.Equal("a\n\nb", BrowserPageMarkdown.Collapse("a\n\n\n\n   \nb\n\n"));

    [Fact]
    public void Page_file_name_uses_the_host_and_is_markdown()
    {
        Assert.Equal("example.com.md", BrowserPageMarkdown.FileNameFor("https://example.com/a/b?c=1"));
        Assert.Equal("page.md", BrowserPageMarkdown.FileNameFor("about:blank"));
    }
}
