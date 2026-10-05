using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services.Terminal;
using sk0ya.Loomo.App.ViewModels;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// コマンドコンポーザ（§23.2）の送信コマンド組み立てと、
/// ペグボード（§23.3）の種別判定・並び・スナップショット往復の検証。
/// </summary>
public class ComposerPegboardTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "loomo-composer-" + Guid.NewGuid().ToString("N"));

    // ===== ComposerCommandBuilder =====

    [Fact]
    public void Single_line_is_sent_as_is_without_writing_script()
    {
        var dir = TempDir();

        var command = ComposerCommandBuilder.Build("  git status  ", dir);

        Assert.Equal("git status", command);
        Assert.False(Directory.Exists(dir)); // 単一行ではスクリプトを書かない
    }

    [Fact]
    public void Multi_line_is_written_to_script_and_invoked()
    {
        var dir = TempDir();
        var text = "# ビルドして失敗だけ見る\ndotnet build |\n  Select-String error";

        var command = ComposerCommandBuilder.Build(text, dir);

        var path = Path.Combine(dir, ComposerCommandBuilder.ScriptFileName);
        Assert.Equal($"& '{path}'", command);
        // コメント・行末パイプを壊さず全文がそのまま書かれている。
        Assert.Equal(text, File.ReadAllText(path));

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Blank_text_yields_nothing_to_send()
    {
        Assert.Null(ComposerCommandBuilder.Build("   \r\n  ", TempDir()));
    }

    // ===== PegboardViewModel =====

    [Theory]
    [InlineData("https://example.com/docs", "url")]
    [InlineData("HTTP://EXAMPLE.COM", "url")]
    [InlineData("ただのメモ", "text")]
    [InlineData("relative\\path.txt", "text")] // 相対パスは text 扱い（実在判定しない）
    public void DetectType_classifies_urls_and_text(string content, string expected)
        => Assert.Equal(expected, PegboardViewModel.DetectType(content));

    [Fact]
    public void DetectType_classifies_existing_rooted_path_as_file()
    {
        var file = Path.GetTempFileName();
        try
        {
            Assert.Equal("file", PegboardViewModel.DetectType(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void DetectType_multiline_is_always_text()
        => Assert.Equal("text", PegboardViewModel.DetectType("https://example.com\n2行目"));

    [Fact]
    public void AddContent_inserts_below_pinned_and_raises_changed()
    {
        var vm = new PegboardViewModel();
        var changed = 0;
        vm.Changed += (_, _) => changed++;

        vm.AddContent("古いメモ");
        vm.TogglePinCommand.Execute(vm.Items[0]); // ピン留め → 先頭固定
        vm.AddContent("新しいメモ");

        Assert.Equal(new[] { "古いメモ", "新しいメモ" }, vm.Items.Select(i => i.Content));
        Assert.True(vm.Items[0].Pinned);
        Assert.Equal(3, changed); // 追加2回＋ピン1回
    }

    [Fact]
    public void Snapshot_roundtrip_preserves_items_and_pins()
    {
        var vm = new PegboardViewModel();
        vm.AddContent("https://example.com", type: "url", title: "Example");
        vm.AddContent("メモ本文");
        vm.TogglePinCommand.Execute(vm.Items.First(i => i.Type == "url"));

        var restored = new PegboardViewModel();
        restored.LoadItems(vm.ToSnapshots());

        Assert.Equal(2, restored.Items.Count);
        // ピン留めが先頭に来る並びで復元される。
        Assert.True(restored.Items[0].Pinned);
        Assert.Equal("url", restored.Items[0].Type);
        Assert.Equal("Example", restored.Items[0].DisplayTitle);
        Assert.Equal("メモ本文", restored.Items[1].Content);
    }

    [Fact]
    public void Material_flow_commands_raise_their_requests()
    {
        var vm = new PegboardViewModel();
        vm.AddContent("dotnet build");
        var item = vm.Items[0];

        PegboardItemVm? toTerminal = null;
        PegboardItemVm? toComposer = null;
        var editorPin = 0;
        vm.SendToTerminalRequested += (_, i) => toTerminal = i;
        vm.SendToComposerRequested += (_, i) => toComposer = i;
        vm.EditorSelectionPinRequested += (_, _) => editorPin++;

        vm.SendToTerminalCommand.Execute(item);
        vm.SendToComposerCommand.Execute(item);
        vm.PinEditorSelectionCommand.Execute(null);

        Assert.Same(item, toTerminal);
        Assert.Same(item, toComposer);
        Assert.Equal(1, editorPin);
    }

    // ===== WorkspaceSnapshot（復元の完全性） =====

    [Fact]
    public void Workspace_snapshot_roundtrips_view_state_fields()
    {
        var path = Path.Combine(TempDir(), "workspaces.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new WorkspaceStateStore(path);

        var state = new WorkspaceState();
        state.Workspaces.Add(new WorkspaceSnapshot
        {
            RootPath = @"C:\Projects\Loomo",
            ComposerVisible = true,
            ComposerHeight = 220,
            Stage = new StageSnapshot { IsActive = true, Pane = PaneKind.Terminal, Overview = true },
            EditorTabs =
            {
                new EditorTabSnapshot
                {
                    FilePath = @"C:\Projects\Loomo\README.md",
                    CaretLine = 41,
                    CaretColumn = 7,
                    ScrollRatio = 0.65,
                },
            },
        });

        store.Save(state);
        var loaded = store.Load();

        var ws = Assert.Single(loaded.Workspaces);
        Assert.True(ws.ComposerVisible);
        Assert.Equal(220, ws.ComposerHeight);
        Assert.True(ws.Stage!.Overview);
        Assert.Equal(PaneKind.Terminal, ws.Stage.Pane);
        var tab = Assert.Single(ws.EditorTabs);
        Assert.Equal(41, tab.CaretLine);
        Assert.Equal(7, tab.CaretColumn);
        Assert.Equal(0.65, tab.ScrollRatio);

        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void Delete_removes_item_and_updates_empty_message()
    {
        var vm = new PegboardViewModel();
        Assert.NotEqual("", vm.EmptyMessage);

        vm.AddContent("一時メモ");
        Assert.Equal("", vm.EmptyMessage);

        vm.DeleteCommand.Execute(vm.Items[0]);
        Assert.Empty(vm.Items);
        Assert.NotEqual("", vm.EmptyMessage);
    }

    // ===== カードの表示（見出し・所在・プレビュー） =====

    private static PegboardItemVm Card(string type, string content, string? title = null)
        => new() { Snapshot = new PegboardItemSnapshot { Type = type, Content = content, Title = title } };

    [Fact]
    public void Url_card_shows_title_with_address_below_or_address_alone()
    {
        var titled = Card("url", "https://example.com/docs/", "Docs");
        Assert.Equal("Docs", titled.DisplayTitle);
        Assert.Equal("example.com/docs", titled.Subtitle);
        Assert.Equal("", titled.Preview);

        var bare = Card("url", "https://example.com/docs");
        Assert.Equal("example.com/docs", bare.DisplayTitle);
        Assert.Equal("", bare.Subtitle); // 見出しと同じ所在は重ねない
    }

    [Fact]
    public void File_card_shows_file_name_with_parent_folder_below()
    {
        var card = Card("file", @"C:\work\app\src\Program.cs");
        Assert.Equal("Program.cs", card.DisplayTitle);
        Assert.Equal(@"C:\work\app\src", card.Subtitle);
        Assert.Equal("", card.Preview);
    }

    [Fact]
    public void Text_card_preview_skips_the_title_line_and_dedents()
    {
        var card = Card("text", "if (x)\n    {\n        Run();\n    }");
        Assert.Equal("if (x)", card.DisplayTitle);
        Assert.Equal("{\n    Run();\n}", card.Preview);

        Assert.Equal("", Card("text", "dotnet build").Preview); // 単一行は見出しだけ
    }

    [Theory]
    [InlineData(0, 0, 30, "たった今")]
    [InlineData(0, 0, 60 * 5, "5分前")]
    [InlineData(0, 3, 0, "3時間前")]
    [InlineData(1, 0, 0, "昨日")]
    [InlineData(4, 0, 0, "4日前")]
    [InlineData(20, 0, 0, "9/15")]
    public void FormatRelative_uses_short_relative_labels(int days, int hours, int seconds, string expected)
    {
        var now = new DateTime(2026, 10, 5, 18, 0, 0);
        var at = now.AddDays(-days).AddHours(-hours).AddSeconds(-seconds);
        Assert.Equal(expected, PegboardItemVm.FormatRelative(at, now));
    }

    [Fact]
    public void Filter_narrows_view_with_and_terms_and_reports_counts()
    {
        var vm = new PegboardViewModel();
        vm.AddContent("dotnet build");
        vm.AddContent("dotnet test");
        vm.AddContent("git status");

        vm.Filter = "dotnet TEST";

        var visible = vm.ItemsView.Cast<PegboardItemVm>().ToList();
        Assert.Equal("dotnet test", Assert.Single(visible).Content);
        Assert.Equal("1 / 3", vm.CountLabel);

        vm.Filter = "nothing";
        Assert.Empty(vm.ItemsView.Cast<PegboardItemVm>());
        Assert.NotEqual("", vm.EmptyMessage);

        vm.CloseFilter();
        Assert.Equal(3, vm.ItemsView.Cast<PegboardItemVm>().Count());
        Assert.Equal("3", vm.CountLabel);
    }

    [Fact]
    public void Section_headers_show_only_when_both_pinned_and_recent_are_visible()
    {
        var vm = new PegboardViewModel();
        vm.AddContent("固定するメモ");
        vm.AddContent("ふつうのメモ");
        Assert.False(vm.ShowSections);

        vm.TogglePinCommand.Execute(vm.Items.First(i => i.Content == "固定するメモ"));
        Assert.True(vm.ShowSections);

        vm.Filter = "ふつう"; // 固定群が絞り込みで消えたら見出しも消す
        Assert.False(vm.ShowSections);
    }

    // ===== ブラウザの抜き書き（出典付き text・§24.24） =====

    [Fact]
    public void Browser_selection_keeps_its_source_and_shows_it_below_the_quote()
    {
        var vm = new PegboardViewModel();
        vm.AddContent("  The quick brown fox  ", type: "text",
                      sourceUrl: "https://example.com/a", sourceTitle: "Example Article");

        var card = Assert.Single(vm.Items);
        Assert.True(card.HasSource);
        Assert.Equal("The quick brown fox", card.DisplayTitle);
        Assert.Equal("出典: Example Article", card.Subtitle);
        Assert.Equal("https://example.com/a#:~:text=The%20quick%20brown%20fox", card.SourceLink);

        // 題名が無ければアドレスで呼ぶ
        vm.AddContent("text", type: "text", sourceUrl: "https://example.com/b", sourceTitle: " ");
        Assert.Equal("出典: example.com/b", vm.Items[0].Subtitle);
    }

    [Fact]
    public void Source_is_kept_only_for_text_and_is_searchable()
    {
        var vm = new PegboardViewModel();
        vm.AddContent("https://example.com/x", type: "url", title: "X", sourceUrl: "https://example.com/page");
        Assert.Null(vm.Items[0].Snapshot.SourceUrl);
        Assert.False(vm.Items[0].HasSource);

        vm.AddContent("引用した一節", type: "text", sourceUrl: "https://docs.example.com/guide", sourceTitle: "ガイド");
        vm.Filter = "ガイド";
        Assert.Equal("引用した一節", Assert.Single(vm.ItemsView.Cast<PegboardItemVm>()).Content);
        vm.Filter = "docs.example";
        Assert.Single(vm.ItemsView.Cast<PegboardItemVm>());
    }

    [Fact]
    public void Open_source_is_requested_only_for_cards_with_a_source()
    {
        var vm = new PegboardViewModel();
        vm.AddContent("メモ");
        vm.AddContent("抜き書き", type: "text", sourceUrl: "https://example.com/", sourceTitle: "E");
        var requested = new List<PegboardItemVm>();
        vm.OpenSourceRequested += (_, item) => requested.Add(item);

        foreach (var item in vm.Items.ToList())
            vm.OpenSourceCommand.Execute(item);

        Assert.Equal("抜き書き", Assert.Single(requested).Content);
    }

    [Theory]
    // 短い1行はまるごと一致。- , & は指令の区切りなので必ずエンコードする
    [InlineData("https://e.com/p", "a-b, c & d", "https://e.com/p#:~:text=a%2Db%2C%20c%20%26%20d")]
    // 既存のフラグメントは残し、既存の Text Fragment は置き換える
    [InlineData("https://e.com/p#sec", "word", "https://e.com/p#sec:~:text=word")]
    [InlineData("https://e.com/p#sec:~:text=old", "new", "https://e.com/p#sec:~:text=new")]
    // http(s) 以外・空の引用はそのまま
    [InlineData("file:///C:/a.html", "word", "file:///C:/a.html")]
    [InlineData("https://e.com/p", " \n ", "https://e.com/p")]
    public void Text_fragment_points_back_to_the_quote(string url, string quote, string expected)
        => Assert.Equal(expected, BrowserTextFragment.Build(url, quote));

    [Fact]
    public void Long_or_multi_paragraph_quote_becomes_a_range_cut_at_word_boundaries()
    {
        var quote = "First paragraph starts here with several words in it\r\n\r\n"
                  + "middle\nThe last paragraph finally ends with these closing words";

        var directive = BrowserTextFragment.Directive(quote);

        Assert.Equal("First%20paragraph%20starts%20here%20with%20several,finally%20ends%20with%20these%20closing%20words", directive);
    }

    [Fact]
    public void Long_quote_without_spaces_is_cut_by_length()
    {
        var quote = new string('あ', 30) + new string('い', 60) + new string('う', 30);

        var directive = BrowserTextFragment.Directive(quote)!;

        var parts = directive.Split(',').Select(Uri.UnescapeDataString).ToArray();
        Assert.Equal(new string('あ', 30) + new string('い', 10), parts[0]);
        Assert.Equal(new string('い', 10) + new string('う', 30), parts[1]);
    }
}
