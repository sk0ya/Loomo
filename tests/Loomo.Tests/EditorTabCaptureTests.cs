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

    /// <summary>抜粋タブ（§35.3）は本文のタブの列に入れず、並びの位置ごと抜粋の列へ残す。復元待ちのまま
    /// 開かれなかった抜粋タブも、空の Untitled として保存されて消えてしまわないこと。</summary>
    [Fact]
    public void A_pending_excerpt_tab_is_kept_in_the_excerpt_list_at_its_position()
    {
        _host.Run(() =>
        {
            var file = RealizedTab(WriteTemp("a.cs", "class A {}\n"));
            var excerpt = new EditorTab(Guid.NewGuid())
            {
                VirtualTitle = "参照: A",
                PendingExcerpt = new ExcerptTabSnapshot
                {
                    Title = "参照: A",
                    Spans = [new ExcerptSpanSnapshot { Path = file.PeekFilePath!, StartLine = 0, EndLine = 0 }],
                },
            };
            Assert.True(excerpt.PeekIsVirtual);
            Assert.Equal("参照: A", excerpt.PeekTitleSource);

            var snapshot = new WorkspaceSnapshot();
            WorkspaceSessionCoordinator.CaptureEditorTabs(snapshot, [file, excerpt], excerpt.Id,
                (tab, index, isActive) => tab.PendingExcerpt is { } p ? Stamp(p, index, isActive) : null);

            Assert.Equal([file.Id], snapshot.EditorTabs.Select(t => t.Id));
            var saved = Assert.Single(snapshot.ExcerptTabs);
            Assert.Equal(1, saved.TabIndex);
            Assert.True(saved.IsActive);
        });

        static ExcerptTabSnapshot Stamp(ExcerptTabSnapshot snapshot, int index, bool isActive)
        {
            snapshot.TabIndex = index;
            snapshot.IsActive = isActive;
            return snapshot;
        }
    }

    [Fact]
    public void Excerpt_tabs_survive_the_workspace_json_round_trip()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"loomo-capture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var workspace = new WorkspaceSnapshot
        {
            RootPath = dir,
            ExcerptTabs =
            [
                new ExcerptTabSnapshot
                {
                    Title = "参照: A", TabIndex = 2, IsActive = true, CaretLine = 3, CaretColumn = 4, ScrollRatio = 0.5,
                    Spans = [new ExcerptSpanSnapshot { Path = @"C:\w\A.cs", StartLine = 10, EndLine = 14 }],
                },
            ],
        };
        using var store = new WorkspaceStateStore(Path.Combine(dir, "workspaces.json"));

        store.Save(new WorkspaceState { ActiveWorkspaceId = workspace.Id, Workspaces = [workspace] });

        var restored = Assert.Single(store.LoadWorkspace(workspace.Id)!.ExcerptTabs);
        Assert.Equal(("参照: A", 2, true, 3, 4), (restored.Title, restored.TabIndex, restored.IsActive, restored.CaretLine, restored.CaretColumn));
        var span = Assert.Single(restored.Spans);
        Assert.Equal((@"C:\w\A.cs", 10, 14), (span.Path, span.StartLine, span.EndLine));
    }

    /// <summary>閉じていた折りたたみもタブと一緒に保存し、開き直したときに戻す（§24.43）。</summary>
    [Fact]
    public void Closed_folds_are_captured_and_restored_with_the_tab()
    {
        _host.Run(() =>
        {
            var path = WriteTemp("folds.txt", string.Join("\n", Enumerable.Range(0, 30).Select(i => $"line {i}")));
            var tab = RealizedTab(path);
            tab.Control.RestoreClosedFolds([new Editor.Core.Folds.FoldRegion(4, 9, IsClosed: true, IsManual: true)]);

            var snapshot = WorkspaceSessionCoordinator.CaptureEditorTab(tab, tab.Id);
            Assert.Equal(new[] { new FoldSnapshot(4, 9, true) }, snapshot.ClosedFolds);

            var restored = new VimEditorControl();
            WorkspaceSessionCoordinator.RestoreEditor(restored, snapshot);

            var fold = Assert.Single(restored.GetClosedFolds());
            Assert.Equal((4, 9, true), (fold.StartLine, fold.EndLine, fold.IsManual));
        });
    }

    [Fact]
    public void A_tab_without_folds_writes_no_fold_list()
    {
        _host.Run(() =>
        {
            var tab = RealizedTab(WriteTemp("plain.txt", "a\nb\n"));
            Assert.Null(WorkspaceSessionCoordinator.CaptureEditorTab(tab, tab.Id).ClosedFolds);
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
