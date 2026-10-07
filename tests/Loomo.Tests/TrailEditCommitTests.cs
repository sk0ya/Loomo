using System.IO;
using Editor.Controls;
using Microsoft.Data.Sqlite;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 軌跡の「編集」は打鍵が 1.5 秒止まってから確定する。確定時に未保存かを見直していたので、その間に保存
/// （Vim の <c>Esc :w</c>、切り離し窓や Diff の右側での保存）されると印が消え、編集が一つも残らなかった。
/// 残すかどうかは本文が変わった時点で決める。確定はタイマーを待たず、別のタブの編集で前のタブが確定する経路で起こす。§27
/// </summary>
[Collection(WpfViewTests.Name)]
public sealed class TrailEditCommitTests : IDisposable
{
    private readonly WpfViewHost _host;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"loomo-trail-edit-{Guid.NewGuid():N}");
    private readonly TrailStore _store;

    public TrailEditCommitTests(WpfViewHost host)
    {
        _host = host;
        Directory.CreateDirectory(_dir);
        _store = new TrailStore(Path.Combine(_dir, "trail.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void An_edit_saved_before_the_commit_delay_is_still_recorded()
    {
        _host.Run(() =>
        {
            var (trail, sut) = Create();
            var a = OpenTab("a.txt");
            Replace(a, "on disk", "edited");
            sut.Request(a);

            a.Control.Save(a.PeekFilePath!);
            Assert.False(a.Control.IsModified);
            EditAnotherTab(sut, "b.txt");

            Assert.Contains(trail.Entries, e => e.Kind == TrailEntryKind.Edit && e.Target == a.PeekFilePath);
        });
    }

    [Fact]
    public void An_edit_undone_back_to_the_saved_text_is_not_recorded()
    {
        _host.Run(() =>
        {
            var (trail, sut) = Create();
            var a = OpenTab("a.txt");
            Replace(a, "on disk", "edited");
            sut.Request(a);
            Replace(a, "edited", "on disk");
            sut.Request(a);   // BufferChanged：保存済みの本文へ戻った

            a.Control.Save(a.PeekFilePath!);
            EditAnotherTab(sut, "b.txt");

            Assert.DoesNotContain(trail.Entries, e => e.Target == a.PeekFilePath);
        });
    }

    [Fact]
    public void Changes_made_while_preparing_a_save_are_not_recorded()
    {
        _host.Run(() =>
        {
            var (trail, sut) = Create();
            var a = OpenTab("a.txt");

            sut.BeginSavePreparation(a.Control);
            Replace(a, "on disk", "formatted");   // 保存時整形が入れた変更
            sut.Request(a);
            sut.EndSavePreparation(a.Control);
            a.Control.Save(a.PeekFilePath!);
            EditAnotherTab(sut, "b.txt");

            Assert.DoesNotContain(trail.Entries, e => e.Target == a.PeekFilePath);
        });
    }

    private (TrailViewModel Trail, TrailEditCommitController Sut) Create()
    {
        var trail = new TrailViewModel(_store, () => new DateTime(2026, 10, 7, 9, 0, 0));
        var sut = new TrailEditCommitController(trail, () => false,
            record => record(DisplayMode.Layout, null, null));
        return (trail, sut);
    }

    private void EditAnotherTab(TrailEditCommitController sut, string name)
    {
        var other = OpenTab(name);
        Replace(other, "on disk", "edited");
        sut.Request(other);   // 前に待っていたタブの編集がここで確定する
    }

    private EditorTab OpenTab(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "on disk\n");
        var control = new VimEditorControl();
        control.LoadFile(path);
        var tab = new EditorTab(Guid.NewGuid());
        tab.SetControl(control);
        return tab;
    }

    private static void Replace(EditorTab tab, string from, string to)
        => Assert.True(tab.Control.TryApplyLspTextEdits(
            [new Editor.Core.Lsp.LspTextEdit(
                new Editor.Core.Lsp.LspRange(
                    new Editor.Core.Lsp.LspPosition(0, 0),
                    new Editor.Core.Lsp.LspPosition(0, from.Length)),
                to)],
            expectedVersion: null, out var error), error);
}
