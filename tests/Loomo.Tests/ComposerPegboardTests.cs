using System;
using System.IO;
using System.Linq;
using sk0ya.Loomo.App.Services;
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
}
