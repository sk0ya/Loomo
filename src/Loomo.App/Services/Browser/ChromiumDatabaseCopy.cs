namespace sk0ya.Loomo.App.Services;

/// <summary>
/// Chromium 系の SQLite（Login Data / Cookies / History）を<b>実体に触らずに</b>読むための一時コピー。
///
/// <para>稼働中のブラウザはこれらを掴んだままなので、開こうとすると失敗するか、
/// 悪くすると相手のプロファイルを壊す。読むときは必ずコピーを作り、読み終えたら消す。
/// 併走ファイル（<c>-wal</c> / <c>-shm</c> / <c>-journal</c>）も一緒に持ってこないと、
/// 直前の書き込みが欠けた古い中身を読むことになる。</para>
/// </summary>
internal static class ChromiumDatabaseCopy
{
    /// <summary>コピー先のフォルダーを作り、そこへ DB 一式を複製してコピー先のパスを返す。</summary>
    public static string To(string sourcePath, string workingDirectory)
    {
        Directory.CreateDirectory(workingDirectory);
        var destination = Path.Combine(workingDirectory, Path.GetFileName(sourcePath));
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var source = sourcePath + suffix;
            if (File.Exists(source))
                CopyShared(source, destination + suffix);
        }
        return destination;
    }

    /// <summary>作業用フォルダーの名前（呼び出しごとに別物にして、並走しても踏み合わないようにする）。</summary>
    public static string NewWorkingDirectory(string tag)
        => Path.Combine(Path.GetTempPath(), $"loomo-{tag}-{Guid.NewGuid():N}");

    /// <summary><see cref="File.Copy(string,string)"/> は掴まれているファイルで失敗することがあるので、
    /// 共有読み取りで開いて自分で流す。</summary>
    private static void CopyShared(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    /// <summary>後始末。消せなくても呼び出し元の処理は済んでいるので、失敗は飲み込む。</summary>
    public static void TryDelete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
