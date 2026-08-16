using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using sk0ya.Loomo.App.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// WebView2 プロファイルに保存されたログイン情報の読み取り（§21.5.2）。
/// Chromium と同じ形のプロファイルをその場で組み立てて、鍵の取り出しから復号までを通す
/// （DPAPI は CurrentUser なので、この検証はテストを走らせた本人の環境で完結する）。
/// </summary>
public class SavedPasswordStoreTests
{
    private static string NewProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"loomo-profile-{Guid.NewGuid():N}", "EBWebView");
        Directory.CreateDirectory(Path.Combine(root, "Default"));
        return root;
    }

    /// <summary>Local State を書き、AES の鍵を返す（Chromium は DPAPI で包んで base64 で持つ）。</summary>
    private static byte[] WriteLocalState(string profileRoot)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var wrapped = Encoding.ASCII.GetBytes("DPAPI")
            .Concat(ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser))
            .ToArray();
        File.WriteAllText(Path.Combine(profileRoot, "Local State"),
            JsonSerializer.Serialize(new { os_crypt = new { encrypted_key = Convert.ToBase64String(wrapped) } }));
        return key;
    }

    /// <summary>v10 形式（プレフィックス3 ＋ ノンス12 ＋ 本体 ＋ タグ16）で暗号化する。</summary>
    private static byte[] EncryptV10(byte[] key, string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var bytes = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[bytes.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, bytes, cipher, tag);
        return Encoding.ASCII.GetBytes("v10").Concat(nonce).Concat(cipher).Concat(tag).ToArray();
    }

    private static void WriteLoginData(string profileRoot, params (string Origin, string User, byte[] Password)[] rows)
        => WriteLoginDataRaw(profileRoot,
            rows.Select(r => (r.Origin, r.User, r.Password, 13300000000000000L)).ToArray());

    private static void WriteLoginDataRaw(
        string profileRoot, params (string Origin, string User, byte[] Password, long Created)[] rows)
    {
        var path = Path.Combine(profileRoot, "Default", "Login Data");
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE logins (origin_url TEXT, username_value TEXT, password_value BLOB, date_created INTEGER)";
            create.ExecuteNonQuery();
        }
        foreach (var (origin, user, password, created) in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO logins (origin_url, username_value, password_value, date_created) VALUES ($o, $u, $p, $d)";
            insert.Parameters.AddWithValue("$o", origin);
            insert.Parameters.AddWithValue("$u", user);
            insert.Parameters.AddWithValue("$p", password);
            insert.Parameters.AddWithValue("$d", created);
            insert.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();   // ファイルを掴んだままだと後片付けで消せない
    }

    [Fact]
    public void 保存済みのログイン情報を復号して読める()
    {
        var profile = NewProfile();
        var key = WriteLocalState(profile);
        WriteLoginData(profile,
            ("https://example.com/login", "taro", EncryptV10(key, "ひみつ")),
            ("https://github.com/login", "hanako", EncryptV10(key, "p@ssw0rd")));

        var result = new SavedPasswordStore(profile).Load();

        Assert.Null(result.Error);
        Assert.Equal(2, result.Items.Count);
        var github = result.Items.Single(p => p.Host == "github.com");
        Assert.Equal("hanako", github.Username);
        Assert.Equal("p@ssw0rd", github.Password);
        Assert.Equal("example.com", result.Items.Single(p => p.Username == "taro").Host);
    }

    /// <summary>古い項目は AES ではなく DPAPI で直接暗号化されている。</summary>
    [Fact]
    public void 古いDPAPI形式の項目も読める()
    {
        var profile = NewProfile();
        WriteLocalState(profile);
        var legacy = ProtectedData.Protect(
            Encoding.UTF8.GetBytes("legacy-secret"), null, DataProtectionScope.CurrentUser);
        WriteLoginData(profile, ("https://old.example.com/", "taro", legacy));

        var result = new SavedPasswordStore(profile).Load();

        Assert.Equal("legacy-secret", Assert.Single(result.Items).Password);
    }

    /// <summary>「保存しない」を選んだサイトは空で入っている——一覧に出しても意味が無い。</summary>
    [Fact]
    public void 中身が空の項目は出さない()
    {
        var profile = NewProfile();
        var key = WriteLocalState(profile);
        WriteLoginData(profile,
            ("https://never.example.com/", "", Array.Empty<byte>()),
            ("https://ok.example.com/", "taro", EncryptV10(key, "x")));

        var result = new SavedPasswordStore(profile).Load();

        Assert.Equal("ok.example.com", Assert.Single(result.Items).Host);
    }

    /// <summary>1件だけ解けない項目（別の鍵で暗号化されている等）が混じっても、残りは出す。</summary>
    [Fact]
    public void 解けない項目があっても残りは読める()
    {
        var profile = NewProfile();
        var key = WriteLocalState(profile);
        var otherKey = RandomNumberGenerator.GetBytes(32);
        WriteLoginData(profile,
            ("https://broken.example.com/", "x", EncryptV10(otherKey, "読めない")),
            ("https://ok.example.com/", "taro", EncryptV10(key, "読める")));

        var result = new SavedPasswordStore(profile).Load();

        Assert.Null(result.Error);
        Assert.Equal("読める", Assert.Single(result.Items).Password);
    }

    /// <summary>「空」と「読めなかった」は必ず区別する——保存したはずなのに一覧が空、が一番困る。</summary>
    [Fact]
    public void プロファイルが無いときは理由を返す()
    {
        var result = new SavedPasswordStore(NewProfile()).Load();

        Assert.Empty(result.Items);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void 鍵が壊れていれば理由を返す()
    {
        var profile = NewProfile();
        File.WriteAllText(Path.Combine(profile, "Local State"), "{\"os_crypt\":{\"encrypted_key\":\"!!!\"}}");
        WriteLoginData(profile, ("https://example.com/", "taro", new byte[] { 1, 2, 3 }));

        var result = new SavedPasswordStore(profile).Load();

        Assert.Empty(result.Items);
        Assert.NotNull(result.Error);
    }

    /// <summary>Login Data はプロファイル作成時からあるのに <c>os_crypt</c> は最初の保存まで書かれない。
    /// つまり「鍵がまだ無い Local State」は普通に起こる——<b>投げずに理由を返す</b>
    /// （呼ぶのは UI スレッドなので、投げれば部屋ごと落ちる）。</summary>
    [Fact]
    public void 暗号鍵がまだ無いLocalStateでも落ちない()
    {
        var profile = NewProfile();
        File.WriteAllText(Path.Combine(profile, "Local State"), "{\"browser\":{}}");
        WriteLoginData(profile, ("https://example.com/", "taro", new byte[] { 1, 2, 3 }));

        var result = new SavedPasswordStore(profile).Load();

        Assert.Empty(result.Items);
        Assert.NotNull(result.Error);
    }

    /// <summary>時刻の桁が壊れた1件のために一覧全体を落とさない（表示に使うだけの値）。</summary>
    [Fact]
    public void 日時が壊れていても読める()
    {
        var profile = NewProfile();
        var key = WriteLocalState(profile);
        WriteLoginDataRaw(profile, ("https://example.com/", "taro", EncryptV10(key, "ひみつ"), long.MaxValue));

        var result = new SavedPasswordStore(profile).Load();

        Assert.Equal("ひみつ", Assert.Single(result.Items).Password);
    }

    /// <summary>UserDataFolder の下の EBWebView がプロファイル一式（WebView2 の作り）。</summary>
    [Fact]
    public void UserDataFolderからプロファイルを組み立てる()
    {
        var profile = NewProfile();
        var userDataFolder = Path.GetDirectoryName(profile)!;
        var key = WriteLocalState(profile);
        WriteLoginData(profile, ("https://example.com/", "taro", EncryptV10(key, "ひみつ")));

        var result = SavedPasswordStore.ForUserDataFolder(userDataFolder).Load();

        Assert.Equal("ひみつ", Assert.Single(result.Items).Password);
    }
}
