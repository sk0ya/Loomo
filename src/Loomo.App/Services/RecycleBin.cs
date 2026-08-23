using System.Runtime.Versioning;
using System.Security.Principal;

namespace sk0ya.Loomo.App.Services;

/// <summary>ゴミ箱へ送った項目を「元の場所」へ戻す（削除の Undo）。
///
/// <para>Windows のゴミ箱には「復元」の公開 API が無い（<c>SHFileOperation</c> の
/// <c>FOF_ALLOWUNDO</c> は“捨てるときに元の場所を覚えておく”だけで、戻す側はシェルの UI 機能）。
/// シェル COM（<c>Shell.Application</c>）から復元の動詞を叩く手もあるが、動詞名がローカライズされる
/// （「元に戻す(&amp;E)」/「Restore」…）ため名前で探す実装になり、環境で壊れる。ここではゴミ箱の実体
/// （<c>&lt;ドライブ&gt;\$Recycle.Bin\&lt;SID&gt;</c>）を直接読む：捨てられた項目は本体が <c>$R…</c>、
/// メタデータ（元のパス・削除日時・サイズ）が同じ接尾辞の <c>$I…</c> という対で置かれているので、
/// 元のパスが一致する <c>$I</c> のうち最も新しいものを選び、対の <c>$R</c> を元の場所へ move して
/// <c>$I</c> を消す＝復元になる。同一ボリューム内の move なので中身のコピーは起きない。</para>
///
/// <para>見つからない（ゴミ箱を経由せず完全削除された・ゴミ箱が空にされた・別ユーザーが消した）
/// 場合は false を返し、呼び出し側がその旨を伝える。ゴミ箱の実体はユーザー自身の SID フォルダなので
/// 管理者権限は要らない。</para></summary>
[SupportedOSPlatform("windows")]
internal static class RecycleBin
{
    /// <summary><paramref name="originalPath"/> にあった項目をゴミ箱から元の場所へ戻す。
    /// 戻せなかったときは false と日本語の理由を返す（例外は投げない）。</summary>
    public static bool TryRestore(string originalPath, out string? error)
    {
        error = null;
        var full = Path.GetFullPath(originalPath);

        if (File.Exists(full) || Directory.Exists(full))
        {
            error = "同じ名前の項目が既にあるため戻せません。";
            return false;
        }

        if (!TryGetBinDirectory(full, out var binDirectory))
        {
            error = "ゴミ箱が見つかりませんでした。";
            return false;
        }

        var match = FindNewest(binDirectory!, full);
        if (match is null)
        {
            error = "ゴミ箱に見つかりませんでした（完全に削除された可能性があります）。";
            return false;
        }

        var payload = Path.Combine(binDirectory!, "$R" + Path.GetFileName(match.InfoPath)[2..]);
        var isDirectory = Directory.Exists(payload);
        if (!isDirectory && !File.Exists(payload))
        {
            error = "ゴミ箱の中の実体が見つかりませんでした。";
            return false;
        }

        try
        {
            // 親フォルダーごと消えている（フォルダーを消してから中のファイルを戻す等）ときは作り直す。
            var parent = Path.GetDirectoryName(full);
            if (parent is not null)
                Directory.CreateDirectory(parent);

            if (isDirectory) Directory.Move(payload, full);
            else File.Move(payload, full);
            File.Delete(match.InfoPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"ゴミ箱から戻せませんでした: {ex.Message}";
            return false;
        }
        return true;
    }

    /// <summary>そのパスの項目が入るゴミ箱フォルダー（ボリュームごと・ログオン中のユーザーの SID）。</summary>
    private static bool TryGetBinDirectory(string fullPath, out string? directory)
    {
        directory = null;
        var volumeRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(volumeRoot))
            return false;

        string sid;
        try { sid = WindowsIdentity.GetCurrent().User?.Value ?? ""; }
        catch { return false; }
        if (sid.Length == 0)
            return false;

        var candidate = Path.Combine(volumeRoot, "$Recycle.Bin", sid);
        if (!Directory.Exists(candidate))
            return false;
        directory = candidate;
        return true;
    }

    /// <summary>元のパスが一致するメタデータのうち、削除日時が最も新しいもの。</summary>
    private static RecycledEntry? FindNewest(string binDirectory, string originalPath)
    {
        RecycledEntry? newest = null;
        IEnumerable<string> infoFiles;
        try { infoFiles = Directory.EnumerateFiles(binDirectory, "$I*"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        foreach (var infoPath in infoFiles)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(infoPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            if (!TryParseInfo(bytes, out var path, out var deletedUtc))
                continue;
            if (!string.Equals(path, originalPath, StringComparison.OrdinalIgnoreCase))
                continue;
            if (newest is null || deletedUtc > newest.DeletedUtc)
                newest = new RecycledEntry(infoPath, deletedUtc);
        }
        return newest;
    }

    /// <summary><c>$I</c> ファイルの中身（元のパスと削除日時）を読む。
    /// 版 1（Vista〜8.1）は元のパスが 260 文字固定、版 2（Windows 10 以降）は文字数が前置される。</summary>
    private static bool TryParseInfo(byte[] bytes, out string path, out DateTime deletedUtc)
    {
        path = "";
        deletedUtc = DateTime.MinValue;
        if (bytes.Length < 28)
            return false;

        var version = BitConverter.ToInt64(bytes, 0);
        try { deletedUtc = DateTime.FromFileTimeUtc(BitConverter.ToInt64(bytes, 16)); }
        catch (ArgumentOutOfRangeException) { deletedUtc = DateTime.MinValue; }

        if (version >= 2)
        {
            var charCount = BitConverter.ToInt32(bytes, 24);   // 終端 NUL を含む文字数
            if (charCount <= 1 || 28 + charCount * 2 > bytes.Length)
                return false;
            path = System.Text.Encoding.Unicode.GetString(bytes, 28, (charCount - 1) * 2);
        }
        else
        {
            const int fixedChars = 260;
            if (24 + fixedChars * 2 > bytes.Length)
                return false;
            path = System.Text.Encoding.Unicode.GetString(bytes, 24, fixedChars * 2).TrimEnd('\0');
        }
        return path.Length > 0;
    }

    private sealed record RecycledEntry(string InfoPath, DateTime DeletedUtc);
}
