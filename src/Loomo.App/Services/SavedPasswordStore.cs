using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace sk0ya.Loomo.App.Services;

/// <summary>保存済みログイン情報の1件。</summary>
public sealed record SavedPassword(string Origin, string Username, string Password, DateTime CreatedUtc)
{
    /// <summary>一覧の見出しに使うホスト名（URL のままだと長くて読めない）。</summary>
    public string Host
    {
        get
        {
            return Uri.TryCreate(Origin, UriKind.Absolute, out var uri) ? uri.Host : Origin;
        }
    }
}

/// <summary>読み取りの結果。読めなかった理由は<b>握りつぶさずに持ち帰る</b>——
/// 「保存したはずなのに一覧が空」が一番困るので、空とエラーは区別する。</summary>
public sealed record SavedPasswordResult(IReadOnlyList<SavedPassword> Items, string? Error)
{
    public static SavedPasswordResult Failed(string message) => new(Array.Empty<SavedPassword>(), message);
}

/// <summary>
/// WebView2 のプロファイルに保存されたログイン情報を読む。
///
/// <para><b>なぜ自前で読むのか</b>：ブラウザペインは <c>IsPasswordAutosaveEnabled</c> を立てているので
/// 保存そのものは Edge が行うが、WebView2 では <c>edge://settings/passwords</c> が開けない——
/// つまり<b>保存はされるのに二度と見られない</b>。ここが埋まらないと「パスワードを覚えさせる」判断ができない。</para>
///
/// <para><b>読み方</b>は Chromium の作りそのままで、
/// <c>Local State</c> の <c>os_crypt.encrypted_key</c>（先頭 5 文字 <c>DPAPI</c> を除いた残りを DPAPI で解錠）が
/// AES-256-GCM の鍵、<c>Default/Login Data</c>（SQLite）の <c>logins.password_value</c> が
/// <c>v10</c>/<c>v11</c> ＋ 12 バイトのノンス ＋ 本体 ＋ 16 バイトのタグ。
/// 古い項目は AES ではなく DPAPI で直接暗号化されているので、そちらも受ける。</para>
///
/// <para><b>書き込まない</b>。Login Data はブラウザが開いている間ずっと掴んでいるので、
/// 消したり書き換えたりするのはプロファイルを壊す道になる。読むときも実体には触らず一時コピーを開く。
/// 一括削除だけは WebView2 の <c>ClearBrowsingDataAsync</c>（＝ブラウザ自身にやらせる）を使う。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SavedPasswordStore(string profileRoot)
{
    private const string KeyPrefix = "DPAPI";
    private static readonly byte[] AesPrefixV10 = "v10"u8.ToArray();
    private static readonly byte[] AesPrefixV11 = "v11"u8.ToArray();

    /// <summary>WebView2 は UserDataFolder の下に <c>EBWebView</c> を作り、その中がプロファイル一式。</summary>
    public static SavedPasswordStore ForUserDataFolder(string userDataFolder)
        => new(Path.Combine(userDataFolder, "EBWebView"));

    private string LocalStatePath => Path.Combine(profileRoot, "Local State");
    private string LoginDataPath => Path.Combine(profileRoot, "Default", "Login Data");

    /// <summary>まだ一度もブラウザを開いていない（プロファイルが無い）ときは、一覧そのものを出さない。</summary>
    public bool IsAvailable => File.Exists(LoginDataPath);

    public SavedPasswordResult Load()
    {
        if (!IsAvailable)
            return SavedPasswordResult.Failed("プロファイルがまだありません。");
        byte[] key;
        try
        {
            key = ReadMasterKey();
        }
        // UnauthorizedAccessException も受ける。Local State が ACL で読めないとここを素通りし、
        // 呼び出し元（Task.Run 内の await）から UI スレッドの未処理例外になる＝一覧を開くだけで落ちる。
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or JsonException or CryptographicException or FormatException)
        {
            return SavedPasswordResult.Failed($"暗号鍵を取り出せませんでした: {ex.Message}");
        }

        // 後始末の対象はコピー先の<b>フォルダー</b>を先に決めておく。CopyDatabase が途中で投げると
        // 戻り値は受け取れないが、そのときには既に Login Data を書き終えていることがある。
        var workingDirectory = Path.Combine(Path.GetTempPath(), $"loomo-logins-{Guid.NewGuid():N}");
        try
        {
            return new SavedPasswordResult(ReadLogins(CopyDatabase(workingDirectory), key), null);
        }
        catch (Exception ex) when (ex is IOException or SqliteException or UnauthorizedAccessException)
        {
            return SavedPasswordResult.Failed($"保存済みの情報を読めませんでした: {ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(workingDirectory);
        }
    }

    /// <summary>Local State の <c>os_crypt.encrypted_key</c> を DPAPI（CurrentUser）で解錠する。
    /// <b>鍵がまだ無い Local State は普通にある</b>——Login Data はプロファイル作成時点で作られるのに対し、
    /// <c>os_crypt</c> は最初に何かを保存するまで書かれない。<c>GetProperty</c> で取ると
    /// <see cref="KeyNotFoundException"/> が飛び、呼び出し元（UI スレッド）ごと落ちる。</summary>
    private byte[] ReadMasterKey()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocalStatePath));
        if (!document.RootElement.TryGetProperty("os_crypt", out var osCrypt)
            || !osCrypt.TryGetProperty("encrypted_key", out var keyElement)
            || keyElement.ValueKind != JsonValueKind.String)
            throw new JsonException("暗号鍵がまだ作られていません。");
        var encoded = keyElement.GetString() ?? throw new JsonException("encrypted_key がありません。");
        var blob = Convert.FromBase64String(encoded);
        if (blob.Length <= KeyPrefix.Length
            || !Encoding.ASCII.GetString(blob, 0, KeyPrefix.Length).Equals(KeyPrefix, StringComparison.Ordinal))
            throw new CryptographicException("鍵の形式が想定と違います。");
        return ProtectedData.Unprotect(blob[KeyPrefix.Length..], null, DataProtectionScope.CurrentUser);
    }

    /// <summary>Login Data は稼働中のブラウザが掴んでいるので、読むのは一時コピー。
    /// 併走ファイル（-wal / -shm / -journal）も一緒に持ってこないと、直前の書き込みが欠ける。</summary>
    private string CopyDatabase(string directory)
    {
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "Login Data");
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var source = LoginDataPath + suffix;
            if (File.Exists(source))
                CopyShared(source, destination + suffix);
        }
        return destination;
    }

    /// <summary><see cref="File.Copy(string,string)"/> は掴まれているファイルで失敗することがあるので、
    /// 共有読み取りで開いて自分で流す。</summary>
    private static void CopyShared(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    private static List<SavedPassword> ReadLogins(string databasePath, byte[] key)
    {
        var items = new List<SavedPassword>();
        // 開くのはコピーなので読み書きで開いてよい（WAL を畳むのに書き込みが要る）。
        // Pooling=false は必須。プールを有効のままだと Dispose 後もプールが接続（＝ファイルハンドル）を
        // 抱えたままになり、この直後の一時フォルダー削除が IOException で落ちる——つまり
        // 「実体に触らず一時コピーを読む」はずが、%TEMP% に資格情報 DB のコピーが溜まり続ける
        // （SqlitePreviewReader も同じ理由で false にしている）。
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT origin_url, username_value, password_value, date_created FROM logins ORDER BY origin_url";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // 1行の型がおかしくても残りは出す（DB の中身はこちらが作ったものではない）。
            try
            {
                var blob = reader.IsDBNull(2) ? Array.Empty<byte>() : (byte[])reader[2];
                if (blob.Length == 0)
                    continue;   // 「保存しない」を選んだサイトは空で入っている
                if (TryDecrypt(blob, key) is not { } password)
                    continue;
                items.Add(new SavedPassword(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    password,
                    FromChromiumTime(reader.IsDBNull(3) ? 0 : reader.GetInt64(3))));
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException)
            {
            }
        }
        return items;
    }

    /// <summary>v10/v11 なら AES-256-GCM、そうでなければ古い DPAPI 直の項目として扱う。</summary>
    private static string? TryDecrypt(byte[] blob, byte[] key)
    {
        try
        {
            var isAes = blob.Length > 15
                && (blob.AsSpan(0, 3).SequenceEqual(AesPrefixV10) || blob.AsSpan(0, 3).SequenceEqual(AesPrefixV11));
            if (!isAes)
                return Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser));
            var nonce = blob.AsSpan(3, 12);
            var tag = blob.AsSpan(blob.Length - 16, 16);
            var cipher = blob.AsSpan(15, blob.Length - 15 - 16);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            // 1件読めなくても残りは出す（アプリ束縛暗号など、こちらで解けない項目が混じり得る）。
            return null;
        }
    }

    /// <summary>Chromium の時刻は 1601-01-01 UTC からのマイクロ秒。
    /// 桁の壊れた値でも <see cref="DateTime"/> の範囲外で投げさせない（表示に使うだけの値で、
    /// これ1件のために一覧全体を落とす意味が無い）。</summary>
    private static DateTime FromChromiumTime(long value)
    {
        if (value <= 0)
            return DateTime.MinValue;
        try { return new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMicroseconds(value); }
        catch (ArgumentOutOfRangeException) { return DateTime.MinValue; }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
