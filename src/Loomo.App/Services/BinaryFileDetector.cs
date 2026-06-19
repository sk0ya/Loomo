using System;
using System.IO;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// ファイルがバイナリかをざっくり判定する。先頭の一定バイト内に NUL(0x00) を含めばバイナリと
/// みなす（git と同じ素朴なヒューリスティック）。テキスト/コードは Editor、バイナリ（画像・PDF 等）は
/// EditorSupport へ振り分ける判断に使う（<see cref="ShellWindow"/> の「ファイルを開く」共通前処理）。
/// </summary>
public static class BinaryFileDetector
{
    // git のバイナリ判定と同じく、先頭 8000 バイトだけを見る。
    private const int SampleSize = 8000;

    /// <summary>バイト列の先頭 <see cref="SampleSize"/> 内に NUL を含めばバイナリ。テスト・再利用用。</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        var length = Math.Min(bytes.Length, SampleSize);
        for (var i = 0; i < length; i++)
            if (bytes[i] == 0)
                return true;
        return false;
    }

    /// <summary>ファイルがバイナリらしければ true。読めない/空ファイルは false（テキスト扱い＝Editor へ）。</summary>
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
            return false;   // 読めなければテキスト扱い（Editor へ）
        }
    }
}
