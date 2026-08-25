using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 取り込むログイン情報の「順番待ち」（設計書 §21.5.4）。
///
/// <para><b>なぜ待たせるのか</b>：<c>Login Data</c> は稼働中の WebView2 が掴んでいて書けない。
/// そして取り込みを操作している瞬間はブラウザペインが動いている＝<b>必ず</b>掴まれている。
/// だから「その場で書く」は原理的に成立しない。ここへ置いて、WebView2 がまだ1つも作られていない
/// <b>次の起動の直後</b>に <see cref="LoginDataWriter.ApplyPending"/> が流し込む。</para>
///
/// <para><b>DPAPI で包んで置く</b>。中身は生のパスワードなので、平文の JSON を
/// <c>%APPDATA%</c> に置いて待たせるわけにはいかない。鍵は CurrentUser——
/// 他のユーザーがファイルを持ち出しても開けない。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PendingPasswordImportStore
{
    private readonly string _path;

    public PendingPasswordImportStore() : this(DefaultPath()) { }

    public PendingPasswordImportStore(string path) => _path = path;

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "pending-password-import.bin");

    /// <summary>待ちが1件でもあるか。<b>起動時の判定をこれだけで済ませる</b>ため、
    /// 復号もせずファイルの有無だけを見る（無いときが大半で、そこに DPAPI を挟む理由が無い）。</summary>
    public bool HasPending => File.Exists(_path);

    /// <summary>積む。既にあるぶんは<b>置き換える</b>——同じ相手から取り込み直したときに
    /// 二重に積み上がると、次の起動でどちらが新しいか分からなくなる（重複は書き込み側の
    /// <c>INSERT OR IGNORE</c> が弾くので、積む側は単純にしておく）。</summary>
    public void Save(IReadOnlyList<ImportedPassword> items)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.SerializeToUtf8Bytes(items);
            File.WriteAllBytes(_path, ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // 積めなくてもブラウズは続けられる。次の取り込みでやり直せる。
        }
    }

    /// <summary>読み出す。壊れていたら<b>空として扱う</b>（起動を止めない）。</summary>
    public IReadOnlyList<ImportedPassword> Load()
    {
        if (!File.Exists(_path))
            return Array.Empty<ImportedPassword>();
        try
        {
            var json = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<List<ImportedPassword>>(json)
                ?? (IReadOnlyList<ImportedPassword>)Array.Empty<ImportedPassword>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or CryptographicException or JsonException)
        {
            return Array.Empty<ImportedPassword>();
        }
    }

    /// <summary>流し込みが済んだので捨てる。<b>中身を上書きしてから消す</b>ほどのことはしない
    /// （DPAPI で包んであり、鍵はこのユーザーのものだから）。</summary>
    public void Clear()
    {
        try { File.Delete(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
