using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// MediaEditorSupport：音声/動画ファイルの拡張子→提供者解決と、file:// URI プロバイダとしての
/// 振る舞い（音声/動画のタイトル判定・大小無視の解決）の検証。
/// </summary>
public class MediaEditorSupportTests
{
    private static EditorSupportRegistry CreateRegistry()
    {
        return new(new IEditorSupportProvider[]
        {
            new BrowserEditorSupport(),
            new MediaEditorSupport()
        });
    }

    [Theory]
    [InlineData(@"C:\work\song.mp3")]
    [InlineData(@"C:\work\take.wav")]
    [InlineData(@"C:\work\voice.ogg")]
    [InlineData(@"C:\work\voice.oga")]
    [InlineData(@"C:\work\track.m4a")]
    [InlineData(@"C:\work\master.flac")]
    [InlineData(@"C:\work\note.opus")]
    [InlineData(@"C:\work\clip.aac")]
    [InlineData(@"C:\work\movie.mp4")]
    [InlineData(@"C:\work\demo.webm")]
    [InlineData(@"C:\work\vertical.m4v")]
    [InlineData(@"C:\work\clip.ogv")]
    public void Resolve_音声動画ファイルにはMediaプロバイダを返す(string path)
    {
        var provider = CreateRegistry().Resolve(path);

        Assert.IsType<MediaEditorSupport>(provider);
    }

    [Theory]
    [InlineData(@"C:\work\SONG.MP3")]
    [InlineData(@"C:\work\MOVIE.MP4")]
    [InlineData(@"C:\work\DEMO.WebM")]
    public void Resolve_大文字拡張子でも解決される(string path)
    {
        var provider = CreateRegistry().Resolve(path);

        Assert.IsType<MediaEditorSupport>(provider);
    }

    [Fact]
    public void MediaSupport_URIプロバイダとしてファイルのfileURIを返す()
    {
        var support = new MediaEditorSupport();

        Assert.IsAssignableFrom<IEditorSupportUriProvider>(support);
        Assert.False(support.UsesEditorText);

        var uri = support.ResolveNavigationUri(@"C:\work\song.mp3");
        Assert.StartsWith("file:///", uri);
        Assert.EndsWith("song.mp3", uri);
    }

    [Fact]
    public void MediaSupport_音声はAudio動画はVideoのタイトルにする()
    {
        var support = new MediaEditorSupport();

        Assert.Equal("Audio: x.mp3", support.DescribeTitle(@"C:\work\x.mp3"));
        Assert.Equal("Video: x.mp4", support.DescribeTitle(@"C:\work\x.mp4"));
    }
}
