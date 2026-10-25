using System.IO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

/// <summary>部屋のタブを最近使った順に巡る（Ctrl+Tab・§24.65）。</summary>
public sealed class RoomTabSwitcherTests
{
    private static readonly DateTime T = new(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void 種類を問わず最近使った順_使っていないタブは最後に見た時刻の順で後ろへ()
    {
        var editor = new RoomTabRef(TabEntryKind.Editor, Guid.NewGuid());
        var terminal = new RoomTabRef(TabEntryKind.Terminal, Guid.NewGuid());
        var browser = new RoomTabRef(TabEntryKind.Browser, Guid.NewGuid());
        var restoredOld = new RoomTabRef(TabEntryKind.Editor, Guid.NewGuid());
        var restoredNew = new RoomTabRef(TabEntryKind.Terminal, Guid.NewGuid());
        var closed = new RoomTabRef(TabEntryKind.Editor, Guid.NewGuid());
        var sut = new RoomTabMru();
        sut.Touch(closed);
        sut.Touch(terminal);
        sut.Touch(browser);
        sut.Touch(editor);
        sut.Touch(terminal);   // ターミナルへ戻った（焦点が入っただけでも数える）

        var live = new[]
        {
            (editor, T), (browser, T), (restoredOld, T.AddHours(-2)), (terminal, T), (restoredNew, T.AddHours(-1)),
        };
        var order = sut.Order(live, x => x.Item1, x => x.Item2).Select(x => x.Item1).ToArray();

        Assert.Equal([terminal, editor, browser, restoredNew, restoredOld], order);   // 閉じたタブは出ない
    }

    [Fact]
    public void ファイルのタブは中身を見せ_保存していないタブとブラウザは説明を出す()
    {
        var file = Path.GetTempFileName();
        try
        {
            var entry = new TabEntryViewModel(TabEntryKind.Editor, "a.cs");
            var withFile = RoomTabSwitcherItems.Editor(entry, file, p => p, () => { });
            Assert.Equal(file, withFile.Target?.FullPath);
            Assert.StartsWith(RoomTabSwitcherItems.EditorCategory, withFile.Category);
            // ワークスペース直下のファイルにはフォルダーを添えない（「エディタ · .」にしない）
            var atRoot = RoomTabSwitcherItems.Editor(entry, file, Path.GetFileName, () => { });
            Assert.Equal(RoomTabSwitcherItems.EditorCategory, atRoot.Category);

            var untitled = RoomTabSwitcherItems.Editor(entry, null, p => p, () => { });
            Assert.Null(untitled.Target);
            Assert.NotNull(untitled.Detail);

            var page = new TabEntryViewModel(TabEntryKind.Browser, "") { BrowserUrl = "https://example.com/" };
            var browser = RoomTabSwitcherItems.Browser(page, () => { });
            Assert.Equal("https://example.com/", browser.Title);
            Assert.Contains("https://example.com/", browser.Detail);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
