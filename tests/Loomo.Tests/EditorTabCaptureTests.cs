using System.IO;
using Editor.Controls;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// スナップショットの採取は打鍵のたびに（UI スレッドで）走る。<c>VimEditorControl.Text</c> は行配列を
/// 毎回 join して全文の複製を作るので、<b>下書きに書くとき＝未保存か名前無しのタブだけ</b>読む。
/// 保存済みのタブはファイルから復元できるため本文を要らない。§31.15
/// </summary>
[Collection(WpfViewTests.Name)]
public sealed class EditorTabCaptureTests
{
    private readonly WpfViewHost _host;

    public EditorTabCaptureTests(WpfViewHost host) => _host = host;

    [Fact]
    public void A_saved_tab_is_captured_without_copying_its_text()
    {
        _host.Run(() =>
        {
            var path = WriteTemp("saved.txt", "on disk\n");
            var tab = RealizedTab(path);

            var snapshot = WorkspaceSessionCoordinator.CaptureEditorTab(tab, tab.Id);

            Assert.Null(snapshot.Text);
            Assert.Equal(path, snapshot.FilePath);
            Assert.False(snapshot.IsModified);
        });
    }

    [Fact]
    public void An_edited_tab_is_captured_with_its_text()
    {
        _host.Run(() =>
        {
            var path = WriteTemp("edited.txt", "on disk\n");
            var tab = RealizedTab(path);
            // SetText は「読み込み」扱いで変更フラグが立たないので、編集として本文を差し替える。
            Assert.True(tab.Control.TryApplyLspTextEdits(
                [new Editor.Core.Lsp.LspTextEdit(
                    new Editor.Core.Lsp.LspRange(
                        new Editor.Core.Lsp.LspPosition(0, 0),
                        new Editor.Core.Lsp.LspPosition(0, "on disk".Length)),
                    "edited in memory")],
                expectedVersion: null, out var error), error);

            var snapshot = WorkspaceSessionCoordinator.CaptureEditorTab(tab, tab.Id);

            Assert.True(snapshot.IsModified);
            Assert.Equal("edited in memory\n", snapshot.Text);
        });
    }

    [Fact]
    public void An_unnamed_tab_is_captured_with_its_text_even_when_it_looks_unmodified()
    {
        _host.Run(() =>
        {
            var control = new VimEditorControl();
            control.SetText("scratch");
            var tab = new EditorTab(Guid.NewGuid());
            tab.SetControl(control);

            var snapshot = WorkspaceSessionCoordinator.CaptureEditorTab(tab, tab.Id);

            Assert.Equal("scratch", snapshot.Text);   // 復元先のファイルが無いので本文が唯一の在り処
        });
    }

    private static EditorTab RealizedTab(string path)
    {
        var control = new VimEditorControl();
        control.LoadFile(path);
        var tab = new EditorTab(Guid.NewGuid());
        tab.SetControl(control);
        return tab;
    }

    private static string WriteTemp(string name, string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"loomo-capture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

}
