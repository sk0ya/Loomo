using System;
using System.IO;

namespace sk0ya.Loomo.Core.Files;

/// <summary>先頭の一定バイト内に NUL を含むファイルをバイナリと判定する。</summary>
public static class BinaryFileDetector
{
    private const int SampleSize = 8000;

    /// <summary>バイト列の先頭サンプル内に NUL を含むか。</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        var length = Math.Min(bytes.Length, SampleSize);
        for (var index = 0; index < length; index++)
            if (bytes[index] == 0)
                return true;
        return false;
    }

    /// <summary>ファイルがバイナリらしければ true。読めないファイルや空ファイルは false。</summary>
    public static bool IsBinary(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[SampleSize];
            var read = stream.Read(buffer, 0, buffer.Length);
            return LooksBinary(buffer.AsSpan(0, read));
        }
        catch
        {
            return false;
        }
    }
}
