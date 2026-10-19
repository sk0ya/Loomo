using System;
using System.IO;
using System.Security.Cryptography;

namespace sk0ya.Loomo.App.Services;

/// <summary>見張っているファイルの変化を受けて、開いているタブをどうするか（§24.57）。</summary>
internal enum ExternalFileChangeAction
{
    /// <summary>何もしない（Loomo 自身が書いた／中身が変わっていない）。</summary>
    None,
    /// <summary>未保存の編集が無いので、そのまま読み直す。</summary>
    Reload,
    /// <summary>未保存の編集があるのにディスク側も変わった：人に選んでもらう。</summary>
    Conflict,
}

/// <summary>
/// 外部変更の判定（§24.57）。比べるのは<b>ディスクのバイト列の指紋</b>で、Loomo が最後に読み込んだ／保存した
/// ときの指紋と今の指紋が同じなら「外では何も起きていない」。本文の文字列で比べないのは、
/// <list type="bullet">
/// <item>自分の保存でも監視は発火する。保存直後に打ち始めたタブは未保存なので、本文比較では
/// 「未保存なのにディスクと違う」＝毎回の保存が競合に見える。</item>
/// <item>Shift_JIS などの文書は <c>File.ReadAllText</c>（UTF-8 既定）で読むと Editor の本文と一致せず、
/// 保存のたびに読み直しが走る。</item>
/// </list>
/// バイト列ならエンコーディングにも改行の綴りにも左右されない。
/// </summary>
internal static class ExternalFileChangePolicy
{
    /// <summary>ファイルの指紋。読めない（消えた・使用中）ときは null。</summary>
    public static string? Fingerprint(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return null;
        }
    }

    /// <param name="isModified">タブに未保存の編集があるか。</param>
    /// <param name="known">Loomo が最後に読み込んだ／保存したときの指紋（分からなければ null）。</param>
    /// <param name="current">いまのディスクの指紋（読めなければ null）。</param>
    public static ExternalFileChangeAction Decide(bool isModified, string? known, string? current)
    {
        // 読めない（書き込み途中・削除された）：次の通知を待つ。消えたファイルをここで閉じたり空にしたりはしない。
        if (current is null)
            return ExternalFileChangeAction.None;
        if (known is not null && string.Equals(known, current, StringComparison.Ordinal))
            return ExternalFileChangeAction.None;
        return isModified ? ExternalFileChangeAction.Conflict : ExternalFileChangeAction.Reload;
    }
}
