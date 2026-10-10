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

    /// <summary>抜粋タブ（§35.3）の編集は、抜粋タブのキャレットではなく位置の解決（＝元ファイルの行）で残す。</summary>
    [Fact]
    public void An_edit_is_recorded_at_the_position_the_resolver_maps_it_to()
    {
        _host.Run(() =>
        {
            var source = Path.Combine(_dir, "source.cs");
            var (trail, sut) = Create(tab => tab.PeekIsVirtual
                ? new TrailFilePosition(source, 41, 3)
                : new TrailFilePosition(tab.PeekFilePath!, tab.Control.Caret.Line, tab.Control.Caret.Column));
            var excerpt = new EditorTab(Guid.NewGuid());
            var view = new VimEditorControl();
            view.OpenVirtualDocument("抜粋", "// ── source.cs\nline", null);
            excerpt.SetControl(view);
            Replace(excerpt, "// ── source.cs", "// ── source.cs!");
            Assert.True(view.IsModified);   // 仮想文書でも編集で未保存になる（Request はこれを見る）
            sut.Request(excerpt);

            EditAnotherTab(sut, "b.txt");   // 待っていた抜粋の編集はここで確定する

            var edit = Assert.Single(trail.Entries, e => e.Kind == TrailEntryKind.Edit && e.Target == source);
            Assert.Equal((source, 41), (edit.Target, edit.Line));
        });
    }

    /// <summary>抜粋タブから元ファイルへ入れた編集かどうかは「元タブの変更通知が抜粋タブの書き込みの最中に来たか」で
    /// 判定している（<c>ExcerptTabController.IsForwarding</c>）。それは BufferChanged が書き込みの中で同期に飛ぶ前提。</summary>
    [Fact]
    public void BufferChanged_is_raised_synchronously_inside_an_applied_edit()
    {
        _host.Run(() =>
        {
            var tab = OpenTab("sync.txt");
            var inside = false;
            var raisedInside = false;
            tab.Control.BufferChanged += (_, _) => raisedInside |= inside;

            inside = true;
            Replace(tab, "on disk", "edited");
            inside = false;

            Assert.True(raisedInside);
        });
    }

    private (TrailViewModel Trail, TrailEditCommitController Sut) Create(
        Func<EditorTab, TrailFilePosition?>? positionOf = null)
    {
        var trail = new TrailViewModel(_store, () => new DateTime(2026, 10, 7, 9, 0, 0));
        var sut = new TrailEditCommitController(trail, () => false,
            record => record(DisplayMode.Layout, null, null), positionOf);
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
