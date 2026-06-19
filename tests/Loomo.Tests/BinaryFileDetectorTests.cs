using System.IO;
using System.Text;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// バイナリ判定（<see cref="BinaryFileDetector"/>）の検証。NUL を含む＝バイナリ（→EditorSupport）、
/// 含まない＝テキスト（→Editor）。ファイルを開くときの左上ペイン切替の振り分けに使う。
/// </summary>
public class BinaryFileDetectorTests
{
    [Fact]
    public void LooksBinary_NULを含めばバイナリ()
    {
        Assert.True(BinaryFileDetector.LooksBinary(new byte[] { 0x89, 0x50, 0x00, 0x4E }));
    }

    [Fact]
    public void LooksBinary_テキストはバイナリでない()
    {
        Assert.False(BinaryFileDetector.LooksBinary(Encoding.UTF8.GetBytes("# Hello\nworld\n")));
    }

    [Fact]
    public void LooksBinary_空はテキスト扱い()
    {
        Assert.False(BinaryFileDetector.LooksBinary(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void IsBinary_存在しないパスはテキスト扱い()
    {
        Assert.False(BinaryFileDetector.IsBinary(Path.Combine(Path.GetTempPath(), "loomo-does-not-exist.xyz")));
    }

    [Fact]
    public void IsBinary_NULを含むファイルはバイナリ()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-bin-{Guid.NewGuid():N}.dat");
        try
        {
            File.WriteAllBytes(path, new byte[] { 0x01, 0x02, 0x00, 0x03 });
            Assert.True(BinaryFileDetector.IsBinary(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsBinary_テキストファイルはテキスト扱い()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-text-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(path, "hello\nworld\n");
            Assert.False(BinaryFileDetector.IsBinary(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
