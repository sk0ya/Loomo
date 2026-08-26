using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Settings;
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
    public void Store_caps_the_snapshot_it_was_given_not_just_the_saved_copy()
    {
        // 保存用のコピーだけ切り詰めると、呼び出し側が持ち続ける実体が無制限に伸び、
        // 候補検索も履歴一覧も毎回その全長を歩くことになる。
        var path = TempFile();
        var snapshot = new BrowserLibrarySnapshot {
            History = {
                new BrowserHistoryEntry { Url = "https://a.example/", LastVisitedUtc = DateTime.UtcNow.AddHours(-2) },
                new BrowserHistoryEntry { Url = "https://b.example/", LastVisitedUtc = DateTime.UtcNow.AddHours(-1) },
                new BrowserHistoryEntry { Url = "https://c.example/", LastVisitedUtc = DateTime.UtcNow },
            },
        };

        new BrowserLibraryStore(path, maxHistory: 2).Save(snapshot);

        Assert.Equal(2, snapshot.History.Count);
        Assert.DoesNotContain(snapshot.History, e => e.Url == "https://a.example/");
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
        Assert.Equal("https://example.com/docs",
            Assert.Single(vm.Bookmarks.OfType<BrowserLinkViewModel>()).Url);

        vm.ToggleBookmark();
        Assert.False(vm.IsBookmarked);
        Assert.Empty(vm.Bookmarks);
        File.Delete(path);
    }

    [Fact]
    public void Multiple_bookmarks_can_be_selected_and_removed_together()
    {
        var path = TempFile();
        var vm = new BrowserViewModel(new BrowserLibraryStore(path));
        vm.RecordVisit("https://example.com/a", "A");
        vm.ToggleBookmark();
        vm.RecordVisit("https://example.com/b", "B");
        vm.ToggleBookmark();

        vm.SelectAllBookmarksCommand.Execute(null);

        Assert.True(vm.IsBookmarkSelectionMode);
        Assert.Equal(2, vm.SelectedBookmarkCount);
        Assert.Equal("選択した 2 件を削除", vm.DeleteSelectedBookmarksText);

        vm.RemoveSelectedBookmarksCommand.Execute(null);

        Assert.Empty(vm.Bookmarks.OfType<BrowserLinkViewModel>());
        Assert.Empty(new BrowserLibraryStore(path).Load().Bookmarks);
        Assert.False(vm.IsBookmarkSelectionMode);
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
        Assert.Single(new BrowserLibraryStore(path).Load().History, h => h.VisitCount == 1);
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
    // ドット無しでもポートが付いていればホスト（社内・開発サーバー）。検索へ流してはいけない。
    [InlineData("devbox:3000", "http://devbox:3000")]
    [InlineData("buildserver:8080/status", "http://buildserver:8080/status")]
    public void Address_gets_a_scheme_when_it_looks_like_a_host(string input, string expected)
        => Assert.Equal(expected, WorkspaceSessionCoordinator.NormalizeBrowserAddress(input, "https://home/"));

    /// <summary>拡張機能の設定画面・ポップアップ（§21.5.2）。既知スキームに無いと、開こうとした設定画面が
    /// まるごと検索語として Google へ流れる。</summary>
    [Fact]
    public void Chrome_extension_urls_pass_through()
        => Assert.Equal(
            "chrome-extension://cjpalhdlnbpafiamejdnhcphjbkeiagm/dashboard.html",
            WorkspaceSessionCoordinator.NormalizeBrowserAddress(
                "chrome-extension://cjpalhdlnbpafiamejdnhcphjbkeiagm/dashboard.html", "https://home/"));

    [Theory]
    [InlineData("loomo とは")]
    [InlineData("wpf")]
    [InlineData("devbox:notaport")]   // 「ホスト:数字」でなければ従来どおり検索語
    // ローカル判定より空白判定が先。ここを逆にすると `http://localhost is down` になって遷移で弾かれる。
    [InlineData("localhost is down")]
    [InlineData("127.0.0.1 not reachable")]
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

    // ===== ブックマークの階層（BrowserBookmarkTree） =====

    private static BrowserBookmark Marked(string url, params string[] folder)
        => new() { Url = url, Folder = folder.ToList() };

    [Fact]
    public void Folders_are_built_from_each_bookmark_place_and_count_the_whole_subtree()
    {
        var tree = BrowserBookmarkTree.Build(new[]
        {
            Marked("https://a.example/", "バー"),
            Marked("https://b.example/", "バー", "開発"),
            Marked("https://c.example/"),
        });

        Assert.Equal("https://c.example/", Assert.Single(tree.Bookmarks).Url);
        var bar = Assert.Single(tree.Folders);
        Assert.Equal("バー", bar.Name);
        Assert.Equal(2, bar.TotalCount);                       // 入れ子のぶんも数える
        Assert.Equal(1, Assert.Single(bar.Folders).TotalCount);
    }

    [Fact]
    public void Collapsed_folders_do_not_produce_rows_for_what_is_inside()
    {
        var tree = BrowserBookmarkTree.Build(new[]
        {
            Marked("https://a.example/", "バー"),
            Marked("https://b.example/", "バー", "開発"),
            Marked("https://c.example/"),
        });

        var collapsed = BrowserBookmarkTree.Flatten(tree, new HashSet<string>());
        Assert.Equal(2, collapsed.Count);                      // 「バー」の行と、一番上の1件
        Assert.Equal("バー", collapsed[0].Folder!.Name);
        Assert.Equal("https://c.example/", collapsed[1].Bookmark!.Url);

        var opened = BrowserBookmarkTree.Flatten(
            tree, new HashSet<string> { BrowserBookmarkTree.Key(new[] { "バー" }) });
        // フォルダーが先、ぶら下がっているブックマークが後。中の「開発」は畳んだまま。
        Assert.Equal(4, opened.Count);
        Assert.Equal("開発", opened[1].Folder!.Name);
        Assert.Equal(1, opened[1].Depth);
        Assert.Equal("https://a.example/", opened[2].Bookmark!.Url);
        Assert.Equal(1, opened[2].Depth);
    }

    [Fact]
    public void A_slash_in_a_folder_name_is_just_a_character_not_a_separator()
    {
        var tree = BrowserBookmarkTree.Build(new[]
        {
            Marked("https://a.example/", "仕事/私用"),
            Marked("https://b.example/", "仕事", "私用"),
        });

        Assert.Equal(2, tree.Folders.Count);
        Assert.Equal("仕事/私用", tree.Folders[0].Name);
        Assert.Empty(tree.Folders[0].Folders);
        Assert.Equal("私用", Assert.Single(tree.Folders[1].Folders).Name);
        Assert.NotEqual(BrowserBookmarkTree.Key(tree.Folders[0].Path),
                        BrowserBookmarkTree.Key(tree.Folders[1].Folders[0].Path));
    }

    [Fact]
    public void Empty_segments_do_not_make_an_unopenable_step()
    {
        var tree = BrowserBookmarkTree.Build(new[] { Marked("https://a.example/", " ", "開発") });

        Assert.Equal("開発", Assert.Single(tree.Folders).Name);
    }

    [Fact]
    public void Folders_start_collapsed_and_open_when_the_row_is_pressed()
    {
        var path = TempFile();
        var store = new BrowserLibraryStore(path);
        store.Save(new BrowserLibrarySnapshot
        {
            Bookmarks =
            {
                new BrowserBookmark { Url = "https://a.example/", Folder = { "バー" } },
                new BrowserBookmark { Url = "https://b.example/" },
            },
        });
        var vm = new BrowserViewModel(store) { IsLibraryOpen = true };

        var folder = Assert.Single(vm.Bookmarks.OfType<BrowserBookmarkFolderViewModel>());
        Assert.False(folder.IsExpanded);
        Assert.Equal(1, folder.Count);
        // 畳んでいる間は中のブックマークの行は無い（一番上の1件だけ）。
        Assert.Equal("https://b.example/",
            Assert.Single(vm.Bookmarks.OfType<BrowserLinkViewModel>()).Url);

        vm.ToggleBookmarkFolderCommand.Execute(folder);

        Assert.True(Assert.Single(vm.Bookmarks.OfType<BrowserBookmarkFolderViewModel>()).IsExpanded);
        Assert.Equal(2, vm.Bookmarks.OfType<BrowserLinkViewModel>().Count());
        File.Delete(path);
    }

    [Fact]
    public void The_place_of_a_bookmark_survives_a_save_and_load()
    {
        var path = TempFile();
        var store = new BrowserLibraryStore(path);
        store.Save(new BrowserLibrarySnapshot
        {
            Bookmarks = { new BrowserBookmark { Url = "https://a.example/", Folder = { "バー", "開発" } } },
        });

        var loaded = new BrowserLibraryStore(path).Load();

        Assert.Equal(new[] { "バー", "開発" }, Assert.Single(loaded.Bookmarks).Folder);
        File.Delete(path);
    }

    [Fact]
    public void Only_bookmark_assets_may_fetch_a_site_icon()
    {
        var path = TempFile();
        var store = new BrowserLibraryStore(path);
        store.Save(new BrowserLibrarySnapshot
        {
            Bookmarks = { new BrowserBookmark { Url = "https://a.example/", Folder = { "バー" } } },
            History = { new BrowserHistoryEntry { Url = "https://b.example/" } },
        });
        var vm = new BrowserViewModel(store) { IsLibraryOpen = true, IsBookmarkBarVisible = true };
        vm.UpdateSuggestions("a.example");

        // 一覧の行と、帯から落ちる一枚の中身は取りに行って良い（人が開いて見えている資産）。
        Assert.All(vm.Bookmarks.OfType<BrowserLinkViewModel>(), row => Assert.True(row.AllowIconFetch));
        Assert.All(Assert.Single(vm.BookmarkBarItems).Children, row => Assert.True(row.AllowIconFetch));

        // 履歴とアドレス欄の候補は手元にあるものだけ。候補には<b>ブックマーク由来の行</b>が
        // 混ざるので、IsBookmark を条件にすると1文字打つたびに取得が走る。
        Assert.All(vm.History, row => Assert.False(row.AllowIconFetch));
        Assert.NotEmpty(vm.Suggestions);
        Assert.Contains(vm.Suggestions, row => row.IsBookmark);
        Assert.All(vm.Suggestions, row => Assert.False(row.AllowIconFetch));
        File.Delete(path);
    }

    // ===== ブックマークバー（アドレス欄の下の帯・表示/非表示） =====

    private static BrowserViewModel WithBookmarks(string path, params BrowserBookmark[] bookmarks)
    {
        var store = new BrowserLibraryStore(path);
        var snapshot = new BrowserLibrarySnapshot();
        snapshot.Bookmarks.AddRange(bookmarks);
        store.Save(snapshot);
        return new BrowserViewModel(store);
    }

    [Fact]
    public void The_bookmark_bar_shows_only_what_sits_at_the_top_and_folds_the_rest_into_folders()
    {
        var path = TempFile();
        var vm = WithBookmarks(path,
            Marked("https://a.example/", "バー"),
            Marked("https://b.example/", "バー", "開発"),
            Marked("https://c.example/"));

        vm.PrepareBookmarkBar();   // ここで browser.json を読む（起動時には読まない）

        // 根の直下＝フォルダー「バー」と、どこにも入っていない1件。
        Assert.Equal(2, vm.BookmarkBarItems.Count);
        var folder = vm.BookmarkBarItems[0];
        Assert.True(folder.IsFolder);
        Assert.Equal("バー", folder.Title);
        // 帯から落ちる一枚は入れ子ぶんも平らに持つ（段は作らない）。
        Assert.Equal(new[] { "https://a.example/", "https://b.example/" },
            folder.Children.Select(c => c.Url));
        Assert.False(vm.BookmarkBarItems[1].IsFolder);
        Assert.Equal("https://c.example/", vm.BookmarkBarItems[1].Url);
    }

    [Fact]
    public void Hiding_the_bookmark_bar_empties_it_and_showing_it_again_fills_it_back()
    {
        var path = TempFile();
        var vm = WithBookmarks(path, Marked("https://a.example/"));
        vm.PrepareBookmarkBar();
        Assert.True(vm.HasBookmarkBarItems);

        vm.ToggleBookmarkBarCommand.Execute(null);

        Assert.False(vm.IsBookmarkBarVisible);
        Assert.Empty(vm.BookmarkBarItems);

        vm.ToggleBookmarkBarCommand.Execute(null);

        Assert.True(vm.IsBookmarkBarVisible);
        Assert.Equal("https://a.example/", Assert.Single(vm.BookmarkBarItems).Url);
        File.Delete(path);
    }

    [Fact]
    public void The_bookmark_bar_visibility_is_kept_in_the_settings()
    {
        var settings = new LoomoSettings();
        var vm = new BrowserViewModel(new BrowserLibraryStore(TempFile()), settings);

        vm.ToggleBookmarkBarCommand.Execute(null);

        Assert.False(settings.BrowserBookmarkBarVisible);

        // 次の起動＝保存された状態で作り直したときに畳んだままであること。
        Assert.False(new BrowserViewModel(new BrowserLibraryStore(TempFile()), settings).IsBookmarkBarVisible);
    }

    [Fact]
    public void Adding_a_bookmark_appears_in_the_bar_without_opening_the_list()
    {
        var path = TempFile();
        var vm = WithBookmarks(path);
        vm.PrepareBookmarkBar();
        vm.SetCurrentPage("https://a.example/", "A");

        vm.ToggleBookmark();

        var item = Assert.Single(vm.BookmarkBarItems);
        Assert.Equal("A", item.Title);
        Assert.Equal("https://a.example/", item.Url);
        File.Delete(path);
    }

    [Fact]
    public void Only_one_folder_hangs_open_from_the_bar_at_a_time()
    {
        var path = TempFile();
        var vm = WithBookmarks(path,
            Marked("https://a.example/", "バー"),
            Marked("https://b.example/", "仕事"));
        vm.PrepareBookmarkBar();

        vm.BookmarkBarItems[0].IsOpen = true;
        vm.BookmarkBarItems[1].IsOpen = true;

        Assert.False(vm.BookmarkBarItems[0].IsOpen);
        Assert.True(vm.BookmarkBarItems[1].IsOpen);
        File.Delete(path);
    }
}
