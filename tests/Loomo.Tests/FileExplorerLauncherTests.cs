using System.IO;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>右クリック「エクスプローラーで開く」（中を開く）と「エクスプローラーで表示」（親で選択）が
/// explorer.exe に渡す引数の検証。二つが同じ動きにならないことが要点。</summary>
public sealed class FileExplorerLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"loomo-explorer-{Guid.NewGuid():N}");

    public FileExplorerLauncherTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 一時フォルダの削除失敗は無視 */ }
    }

    [Fact]
    public void フォルダーを開くとその中を開き_表示では親で選択する()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;

        Assert.Equal(folder, FileExplorerLauncher.ExplorerArgument(folder, reveal: false));
        Assert.Equal("/select," + folder, FileExplorerLauncher.ExplorerArgument(folder + "\\", reveal: true));
    }

    [Fact]
    public void ファイルは開いても表示しても親フォルダーで選択する()
    {
        var file = Path.Combine(_root, "note.md");
        File.WriteAllText(file, "x");

        Assert.Equal("/select," + file, FileExplorerLauncher.ExplorerArgument(file, reveal: true));
        Assert.Equal("/select," + file, FileExplorerLauncher.ExplorerArgument(file, reveal: false));
    }

    [Fact]
    public void ドライブ直下は選択せずそのまま開く()
    {
        var drive = Path.GetPathRoot(_root)!;

        Assert.Equal(drive, FileExplorerLauncher.ExplorerArgument(drive, reveal: true));
    }

    [Fact]
    public void シェル名前空間は開けるが表示はしない()
    {
        const string guid = ":::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

        Assert.Equal("shell" + guid, FileExplorerLauncher.ExplorerArgument(guid, reveal: false));
        Assert.Equal("shell" + guid, FileExplorerLauncher.ExplorerArgument("shell" + guid, reveal: false));
        Assert.Null(FileExplorerLauncher.ExplorerArgument(guid, reveal: true));
    }

    [Fact]
    public void 存在しないパスは何もしない()
        => Assert.Null(FileExplorerLauncher.ExplorerArgument(Path.Combine(_root, "missing"), reveal: false));

    [Fact]
    public void 余白の右クリックだけに現在フォルダーの項目を出す()
    {
        var none = new FileContextMenuState(0, 0, false, false, false, false, false, false, false, false, false, false, false);

        Assert.True(FileContextMenuPolicy.IsVisible("Background", none, historyVisible: false));
        Assert.False(FileContextMenuPolicy.IsVisible("Background", none with { SelectionCount = 1 }, historyVisible: false));
    }
}
