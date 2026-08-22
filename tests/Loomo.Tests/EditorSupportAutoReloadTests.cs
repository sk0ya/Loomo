using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// プレビューの自動リロード（§24.8）の判断。ここが緩むと<b>隠れているペインや別ファイルのために
/// WebView2 を読み直す</b>ことになり、逆に厳し過ぎると「保存したのに古いまま」に戻る。
/// 実際の <see cref="System.IO.FileSystemWatcher"/> の発火に依らずに確かめられるよう、
/// 判断だけを純粋関数へ切り出してある。
/// </summary>
public class EditorSupportAutoReloadTests
{
    private const string Html = @"C:\work\docs\index.html";

    /// <summary>URI 提供者ではない（本文から HTML を組む）提供者の代役。</summary>
    private sealed class TextProvider : IEditorSupportProvider
    {
        public IReadOnlyCollection<string> SupportedExtensions => new[] { ".md" };
        public string DescribeTitle(string filePath) => filePath;
    }

    /// <summary>宣言だけを差し替えられる URI 提供者の代役。</summary>
    private sealed class UriProvider : IEditorSupportUriProvider
    {
        public bool Reload { get; init; }
        public IReadOnlyCollection<string> SupportedExtensions => new[] { ".html" };
        public string DescribeTitle(string filePath) => filePath;
        public string ResolveNavigationUri(string filePath) => new Uri(filePath).AbsoluteUri;
        public bool ReloadOnFileChange => Reload;
    }

    [Fact]
    public void 自動リロードを宣言したURI提供者だけを見張る()
    {
        // 宣言するのは提供者側（ホストに拡張子の一覧を置かない）。
        Assert.Equal(Html, EditorSupportAutoReload.WatchTarget(new BrowserEditorSupport(), Html, true));

        // 再生中の動画が頭出しへ戻る／描きかけのキャンバスが消えるものは見張らない。
        Assert.Null(EditorSupportAutoReload.WatchTarget(new MediaEditorSupport(), @"C:\work\a.mp4", true));
        Assert.Null(EditorSupportAutoReload.WatchTarget(new PochiEditorSupport(), @"C:\work\a.pochi.json", true));

        // URI 提供者でない（編集中の本文へ追従する）提供者は、そもそもディスクを見る必要が無い。
        Assert.Null(EditorSupportAutoReload.WatchTarget(new TextProvider(), @"C:\work\a.md", true));
        Assert.Null(EditorSupportAutoReload.WatchTarget(null, Html, true));
    }

    [Fact]
    public void ペインが見えていないなら見張らない()
    {
        // 取りこぼしより「張りっぱなし」を避ける。隠れている間の更新は、次に表示されたときの描画で拾われる。
        Assert.Null(EditorSupportAutoReload.WatchTarget(new BrowserEditorSupport(), Html, paneShowing: false));
    }

    [Fact]
    public void 対象ファイルが無ければ見張らない()
    {
        Assert.Null(EditorSupportAutoReload.WatchTarget(new BrowserEditorSupport(), null, true));
        Assert.Null(EditorSupportAutoReload.WatchTarget(new BrowserEditorSupport(), "   ", true));
    }

    [Fact]
    public void 表示中のファイルの変更なら読み直す()
    {
        Assert.True(EditorSupportAutoReload.ShouldReload(Html, new BrowserEditorSupport(), Html, true));
    }

    [Fact]
    public void 書き方の違う同じパスも同じファイルとして扱う()
    {
        // 監視は FullPath で通知してくるので、表示中のパスの綴りと必ずしも一致しない。
        Assert.True(EditorSupportAutoReload.ShouldReload(
            @"C:\work\docs\..\docs\INDEX.HTML", new BrowserEditorSupport(), Html, true));
    }

    [Fact]
    public void 別のファイルの変更では読み直さない()
    {
        // 親ディレクトリを張る都合で、同じフォルダーの別ファイルが届きうる（Filter をすり抜ける 8.3 名など）。
        Assert.False(EditorSupportAutoReload.ShouldReload(
            @"C:\work\docs\other.html", new BrowserEditorSupport(), Html, true));
        Assert.False(EditorSupportAutoReload.ShouldReload(null, new BrowserEditorSupport(), Html, true));
    }

    [Fact]
    public void 通知が届くまでに追従先が変わっていたら読み直さない()
    {
        // 監視の発火→デバウンス→UI スレッド、の間に別ファイルへ切り替わることがある。
        // 判断材料をそのときの状態から取り直しているので、古い通知はここで落ちる。
        Assert.False(EditorSupportAutoReload.ShouldReload(
            Html, new BrowserEditorSupport(), @"C:\work\docs\next.html", true));
    }

    [Fact]
    public void 通知が届くまでにペインが隠れていたら読み直さない()
    {
        Assert.False(EditorSupportAutoReload.ShouldReload(
            Html, new BrowserEditorSupport(), Html, paneShowing: false));
    }

    [Fact]
    public void 宣言を下ろした提供者は届いても読み直さない()
    {
        // 見張るかどうかと読み直すかどうかが同じ宣言を見ている（片方だけ効く、が起きない）。
        Assert.Null(EditorSupportAutoReload.WatchTarget(new UriProvider { Reload = false }, Html, true));
        Assert.False(EditorSupportAutoReload.ShouldReload(Html, new UriProvider { Reload = false }, Html, true));
        Assert.True(EditorSupportAutoReload.ShouldReload(Html, new UriProvider { Reload = true }, Html, true));
    }
}
