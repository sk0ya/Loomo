using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// Chromium 系プロファイル（Chrome / Vivaldi / Edge / WebView2 …どれも同じ作り）の
/// <c>os_crypt</c> 鍵を開けて、保存値を解く／作る。
///
/// <para><b>1か所にまとめる理由</b>：この暗号の綴りは元々 <see cref="SavedPasswordStore"/> の中だけにあったが、
/// 取り込み（<see cref="ChromiumImportReader"/>）で<b>他所のブラウザのプロファイル</b>を読み、
/// さらに <see cref="LoginDataWriter"/> で<b>こちらのプロファイルへ書く</b>ようになった。
/// 読みと書きで v10 の綴りが1バイトでもずれると、保存できたように見えて二度と解けない
/// 資格情報ができあがる——だから鍵も形式もここにしか無い。</para>
///
/// <para><b>形式</b>：<c>Local State</c> の <c>os_crypt.encrypted_key</c> は先頭 5 文字が <c>DPAPI</c> で、
/// 残りを DPAPI（CurrentUser）で解くと AES-256-GCM の鍵。値のほうは
/// <c>v10</c>/<c>v11</c> ＋ ノンス 12 ＋ 本体 ＋ タグ 16。プレフィックスが無い古い項目は DPAPI 直。</para>
///
/// <para><b><c>v20</c>（アプリ束縛暗号／App-Bound Encryption）は解けない</b>——これは Chrome 127 以降が
/// 使う形式で、鍵は Chrome 自身の COM サービスが<b>呼び出し元の実行ファイルを検証してから</b>しか渡さない。
/// 同じユーザー権限でも外から取れないのが仕様で、迂回は他アプリの保護を破ることになるのでやらない。
/// 見分けは <see cref="IsAppBound"/>（<c>Local State</c> に <c>app_bound_encrypted_key</c> があるか）と、
/// 値ごとの <see cref="IsAppBoundValue"/>。取り込み側はこれを見て「この項目は移せない」と<b>正直に言う</b>。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ChromiumCrypto
{
    private const string KeyPrefix = "DPAPI";
    private static readonly byte[] AesPrefixV10 = "v10"u8.ToArray();
    private static readonly byte[] AesPrefixV11 = "v11"u8.ToArray();
    private static readonly byte[] AppBoundPrefixV20 = "v20"u8.ToArray();

    private readonly byte[] _key;

    private ChromiumCrypto(byte[] key, bool isAppBound)
    {
        _key = key;
        IsAppBound = isAppBound;
    }

    /// <summary>このプロファイルはアプリ束縛暗号を使っている（＝新しく保存された項目は解けない）。
    /// 鍵自体は従来のものも残っているので、古い項目だけは読めることがある。</summary>
    public bool IsAppBound { get; }

    /// <summary><c>Local State</c> を持つフォルダー（Chrome/Vivaldi なら <c>User Data</c>、
    /// WebView2 なら <c>EBWebView</c>）を開く。開けない理由は握り潰さず文字列で返す。</summary>
    public static ChromiumCrypto Open(string profileRoot)
    {
        var path = Path.Combine(profileRoot, "Local State");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("os_crypt", out var osCrypt))
            throw new JsonException("暗号鍵がまだ作られていません。");
        var isAppBound = osCrypt.TryGetProperty("app_bound_encrypted_key", out _);
        if (!osCrypt.TryGetProperty("encrypted_key", out var keyElement)
            || keyElement.ValueKind != JsonValueKind.String)
            throw new JsonException("暗号鍵がまだ作られていません。");
        var encoded = keyElement.GetString() ?? throw new JsonException("encrypted_key がありません。");
        var blob = Convert.FromBase64String(encoded);
        if (blob.Length <= KeyPrefix.Length
            || !Encoding.ASCII.GetString(blob, 0, KeyPrefix.Length).Equals(KeyPrefix, StringComparison.Ordinal))
            throw new CryptographicException("鍵の形式が想定と違います。");
        return new ChromiumCrypto(
            ProtectedData.Unprotect(blob[KeyPrefix.Length..], null, DataProtectionScope.CurrentUser),
            isAppBound);
    }

    /// <summary>開けなかったときに例外ではなく理由を持ち帰る版（一覧を1件の失敗で落とさないため）。</summary>
    public static bool TryOpen(string profileRoot, out ChromiumCrypto? crypto, out string? error)
    {
        try
        {
            crypto = Open(profileRoot);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or JsonException or CryptographicException or FormatException)
        {
            crypto = null;
            error = $"暗号鍵を取り出せませんでした: {ex.Message}";
            return false;
        }
    }

    /// <summary>この値はアプリ束縛暗号（<c>v20</c>）で、こちらでは解けない。</summary>
    public static bool IsAppBoundValue(ReadOnlySpan<byte> blob)
        => blob.Length >= 3 && blob[..3].SequenceEqual(AppBoundPrefixV20);

    /// <summary>復号して UTF-8 文字列にする。解けなければ <c>null</c>（1件の失敗で全体を止めない）。</summary>
    public string? TryDecryptText(byte[] blob)
        => TryDecrypt(blob) is { } plain ? Encoding.UTF8.GetString(plain) : null;

    /// <summary>復号して生バイトで返す（Cookie はドメインのハッシュが前置されるので、文字列にする前に剥がす）。</summary>
    public byte[]? TryDecrypt(byte[] blob)
    {
        try
        {
            if (blob.Length == 0 || IsAppBoundValue(blob))
                return null;
            var isAes = blob.Length > 15
                && (blob.AsSpan(0, 3).SequenceEqual(AesPrefixV10) || blob.AsSpan(0, 3).SequenceEqual(AesPrefixV11));
            if (!isAes)
                return ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser);
            var nonce = blob.AsSpan(3, 12);
            var tag = blob.AsSpan(blob.Length - 16, 16);
            var cipher = blob.AsSpan(15, blob.Length - 15 - 16);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return plain;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Cookie の値を解く。M118 以降の Chromium は平文の先頭に
    /// <b>host_key の SHA-256（32 バイト）</b>を付けてから暗号化するので、一致したぶんだけ剥がす
    /// （<b>長さで決め打ちしない</b>——付いていない古い項目の頭 32 バイトを削ると値が静かに壊れる）。</summary>
    public string? TryDecryptCookie(byte[] blob, string hostKey)
    {
        if (TryDecrypt(blob) is not { } plain)
            return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(hostKey));
        return plain.Length >= hash.Length && plain.AsSpan(0, hash.Length).SequenceEqual(hash)
            ? Encoding.UTF8.GetString(plain, hash.Length, plain.Length - hash.Length)
            : Encoding.UTF8.GetString(plain);
    }

    /// <summary>v10 形式で包む（取り込んだパスワードをこちらのプロファイルへ書くときに使う）。</summary>
    public byte[] EncryptV10(string plain)
    {
        var bytes = Encoding.UTF8.GetBytes(plain);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[bytes.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, bytes, cipher, tag);
        var result = new byte[3 + nonce.Length + cipher.Length + tag.Length];
        AesPrefixV10.CopyTo(result, 0);
        nonce.CopyTo(result, 3);
        cipher.CopyTo(result, 3 + nonce.Length);
        tag.CopyTo(result, 3 + nonce.Length + cipher.Length);
        return result;
    }

    /// <summary>Chromium の時刻は 1601-01-01 UTC からのマイクロ秒。桁の壊れた値で落とさない。</summary>
    public static DateTime FromChromiumTime(long value)
    {
        if (value <= 0)
            return DateTime.MinValue;
        try { return Epoch.AddMicroseconds(value); }
        catch (ArgumentOutOfRangeException) { return DateTime.MinValue; }
    }

    /// <summary><see cref="FromChromiumTime"/> の逆（書き込む行の日時に使う）。</summary>
    public static long ToChromiumTime(DateTime utc)
    {
        if (utc <= Epoch)
            return 0;
        return (long)(utc - Epoch).TotalMicroseconds;
    }

    private static readonly DateTime Epoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);
}
