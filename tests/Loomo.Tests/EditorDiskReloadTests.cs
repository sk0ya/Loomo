using System.IO;
using Editor.Controls;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 開いたままのタブへの読み直し（§24.57）。<c>LoadFile</c> を同じパスでもう一度呼んでも Editor はディスクを
/// 読まない——以前の読み直しはそれで黙って何もしていなかった。Loomo は <c>ReloadFromDisk</c> で入れ直す
/// （元に戻す履歴とカーソルが残ることは Editor 側の FileWatchOptionTests が確かめる）。
/// </summary>
[Collection(WpfViewTests.Name)]
public sealed class EditorDiskReloadTests
{
    private readonly WpfViewHost _host;

    public EditorDiskReloadTests(WpfViewHost host) => _host = host;

    [Fact]
    public void 同じパスのLoadFileはディスクを読み直さない_だから読み直しには使わない()
    {
        _host.Run(() =>
        {
            var path = WriteTemp("a.txt", "one\n");
            var control = new VimEditorControl();
            control.LoadFile(path);
            File.WriteAllText(path, "two\n");

            control.LoadFile(path);
            Assert.Equal("one\n", control.Text);

            Assert.True(control.ReloadFromDisk());
            Assert.Equal("two\n", control.Text);
        });
    }

    [Fact]
    public void 読み直しは未保存の編集を元に戻す履歴へ残して_保存済みに揃える()
    {
        _host.Run(() =>
        {
            var path = WriteTemp("c.txt", "disk\n");
            var control = new VimEditorControl();
            control.LoadFile(path);
            // SetText は「読み込み」扱いで変更フラグが立たないので、編集として本文を差し替える。
            Assert.True(control.TryApplyLspTextEdits(
                [new Editor.Core.Lsp.LspTextEdit(
                    new Editor.Core.Lsp.LspRange(
                        new Editor.Core.Lsp.LspPosition(0, 0),
                        new Editor.Core.Lsp.LspPosition(0, "disk".Length)),
                    "edited in memory")],
                expectedVersion: null, out var error), error);
            File.WriteAllText(path, "outside\n");

            Assert.True(control.ReloadFromDisk());

            Assert.Equal("outside\n", control.Text);
            Assert.False(control.IsModified);
            control.ExecuteCommand("undo");
            Assert.Equal("edited in memory\n", control.Text);
        });
    }

    private static string WriteTemp(string name, string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"loomo-reload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }
}
