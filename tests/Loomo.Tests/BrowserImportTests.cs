using System;
using System.Collections.Generic;
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
/// 他のブラウザからの取り込み（§21.5.4）の検証。
///
/// <para>Chromium と同じ形のプロファイルをその場で組み立てて、
/// <b>読む→混ぜる→書く→読み直す</b>まで通す。とくに書き込み（<see cref="LoginDataWriter"/>）は
/// 「保存できたように見えて二度と解けない」が最悪の壊れ方なので、
/// <see cref="SavedPasswordStore"/> で読み直せることをもって検証とする。</para>
/// </summary>
public class BrowserImportTests
{
    // ===== 足場 =====

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Local State を書いて AES の鍵を返す（Chromium は DPAPI で包んで base64 で持つ）。</summary>
    private static byte[] WriteLocalState(string profileRoot, bool appBound = false)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var wrapped = Encoding.ASCII.GetBytes("DPAPI")
            .Concat(ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser))
            .ToArray();
        Directory.CreateDirectory(profileRoot);
        var osCrypt = new Dictionary<string, object> { ["encrypted_key"] = Convert.ToBase64String(wrapped) };
        if (appBound)
            osCrypt["app_bound_encrypted_key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(Path.Combine(profileRoot, "Local State"),
            JsonSerializer.Serialize(new { os_crypt = osCrypt }));
        return key;
    }

    private static byte[] EncryptV10(byte[] key, byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Encoding.ASCII.GetBytes("v10").Concat(nonce).Concat(cipher).Concat(tag).ToArray();
    }

    // ===== 暗号（ChromiumCrypto） =====

    [Fact]
    public void Encrypted_password_survives_a_round_trip()
    {
        var root = NewDirectory();
        WriteLocalState(root);
        var crypto = ChromiumCrypto.Open(root);

        var blob = crypto.EncryptV10("ひみつ-p@ss");

        Assert.Equal("v10", Encoding.ASCII.GetString(blob, 0, 3));
        Assert.Equal("ひみつ-p@ss", crypto.TryDecryptText(blob));
    }

    [Fact]
    public void App_bound_value_is_refused_rather_than_guessed()
    {
        var root = NewDirectory();
        WriteLocalState(root);
        var crypto = ChromiumCrypto.Open(root);

        var v20 = Encoding.ASCII.GetBytes("v20").Concat(RandomNumberGenerator.GetBytes(40)).ToArray();

        Assert.True(ChromiumCrypto.IsAppBoundValue(v20));
        Assert.Null(crypto.TryDecryptText(v20));
    }

    [Fact]
    public void App_bound_profile_is_recognised_from_local_state()
    {
        var plain = NewDirectory();
        WriteLocalState(plain);
        var bound = NewDirectory();
        WriteLocalState(bound, appBound: true);

        Assert.False(ChromiumCrypto.Open(plain).IsAppBound);
        Assert.True(ChromiumCrypto.Open(bound).IsAppBound);
    }

    /// <summary>M118 以降の Cookie は平文の先頭に host_key の SHA-256 が付く。剥がさないと
    /// 値の頭に 32 バイトのゴミが乗ったまま入る。</summary>
    [Fact]
    public void Cookie_value_drops_the_domain_hash_prefix()
    {
        var root = NewDirectory();
        var key = WriteLocalState(root);
        var crypto = ChromiumCrypto.Open(root);
        var host = "example.com";
        var plain = SHA256.HashData(Encoding.UTF8.GetBytes(host))
            .Concat(Encoding.UTF8.GetBytes("session=abc")).ToArray();

        Assert.Equal("session=abc", crypto.TryDecryptCookie(EncryptV10(key, plain), host));
    }

    /// <summary>前置ハッシュが付いていない古い項目の頭を削ってはいけない（長さで決め打ちしない）。</summary>
    [Fact]
    public void Cookie_without_prefix_keeps_every_byte()
    {
        var root = NewDirectory();
        var key = WriteLocalState(root);
        var crypto = ChromiumCrypto.Open(root);
        var value = new string('x', 40);

        Assert.Equal(value, crypto.TryDecryptCookie(EncryptV10(key, Encoding.UTF8.GetBytes(value)), "example.com"));
    }

    // ===== Cookie の読み取り =====

    /// <summary>Cookie の DB を Chromium と同じ形で組み立てる（列は実機の版に合わせた最小集合）。</summary>
    private static void WriteCookies(
        string profilePath, byte[] key,
        params (string Host, string Name, string Value, long Expires, long Persistent, string Partition)[] rows)
    {
        var directory = Path.Combine(profilePath, "Network");
        Directory.CreateDirectory(directory);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "Cookies")}");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
            CREATE TABLE cookies (
                host_key TEXT NOT NULL, top_frame_site_key TEXT NOT NULL DEFAULT '', name TEXT NOT NULL,
                value TEXT NOT NULL, encrypted_value BLOB, path TEXT NOT NULL, expires_utc INTEGER NOT NULL,
                is_secure INTEGER NOT NULL, is_httponly INTEGER NOT NULL, samesite INTEGER NOT NULL,
                is_persistent INTEGER NOT NULL)
            """;
            create.ExecuteNonQuery();
        }
        foreach (var (host, name, value, expires, persistent, partition) in rows)
        {
            // 実機と同じく「ドメインの SHA-256 ＋ 値」を暗号化する。
            var plain = SHA256.HashData(Encoding.UTF8.GetBytes(host))
                .Concat(Encoding.UTF8.GetBytes(value)).ToArray();
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO cookies (host_key, top_frame_site_key, name, value, encrypted_value, path, "
                + "expires_utc, is_secure, is_httponly, samesite, is_persistent) "
                + "VALUES ($h, $t, $n, '', $e, '/', $x, 1, 1, 1, $p)";
            insert.Parameters.AddWithValue("$h", host);
            insert.Parameters.AddWithValue("$t", partition);
            insert.Parameters.AddWithValue("$n", name);
            insert.Parameters.AddWithValue("$e", EncryptV10(key, plain));
            insert.Parameters.AddWithValue("$x", expires);
            insert.Parameters.AddWithValue("$p", persistent);
            insert.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Cookies_are_read_and_decrypted()
    {
        var profile = NewDirectory();
        var key = WriteLocalState(profile);
        var future = ChromiumCrypto.ToChromiumTime(DateTime.UtcNow.AddDays(30));
        WriteCookies(profile, key, ("example.com", "sid", "abc123", future, 1, ""));

        var read = ChromiumImportReader.ReadCookies(profile, ChromiumCrypto.Open(profile));

        var cookie = Assert.Single(read.Items);
        Assert.Equal("abc123", cookie.Value);
        Assert.Equal("example.com", cookie.Domain);
        Assert.True(cookie.IsSecure);
        Assert.True(cookie.IsHttpOnly);
        Assert.NotNull(cookie.ExpiresUtc);
    }

    /// <summary>期限切れは渡しても相手が捨てるので、渡す前に外して数える
    /// （外さないと「N 件取り込みました」の N が実際と食い違う）。</summary>
    [Fact]
    public void Expired_cookies_are_counted_not_handed_over()
    {
        var profile = NewDirectory();
        var key = WriteLocalState(profile);
        WriteCookies(profile, key,
            ("example.com", "old", "x", ChromiumCrypto.ToChromiumTime(DateTime.UtcNow.AddDays(-1)), 1, ""),
            ("example.com", "new", "y", ChromiumCrypto.ToChromiumTime(DateTime.UtcNow.AddDays(1)), 1, ""));

        var read = ChromiumImportReader.ReadCookies(profile, ChromiumCrypto.Open(profile));

        Assert.Equal("new", Assert.Single(read.Items).Name);
        Assert.Equal(1, read.Skipped);
    }

    /// <summary>区画付き（CHIPS）は区画を落として入れると「本来通用しない場所で通る」Cookie に
    /// なるので持ち込まない。<see cref="ImportRead{T}.Blocked"/> ではなく <c>Skipped</c> で数える。</summary>
    [Fact]
    public void Partitioned_cookies_are_left_behind()
    {
        var profile = NewDirectory();
        var key = WriteLocalState(profile);
        var future = ChromiumCrypto.ToChromiumTime(DateTime.UtcNow.AddDays(30));
        WriteCookies(profile, key,
            ("ads.example", "id", "x", future, 1, "https://news.example"),
            ("example.com", "sid", "y", future, 1, ""));

        var read = ChromiumImportReader.ReadCookies(profile, ChromiumCrypto.Open(profile));

        Assert.Equal("sid", Assert.Single(read.Items).Name);
        Assert.Equal(1, read.Skipped);
        Assert.Equal(0, read.Blocked);
    }

    /// <summary>セッション Cookie（期限なし）は持ち込む。DB には残らないが、
    /// 入れること自体は正しい——「ログインしたまま」の一部はこれ。</summary>
    [Fact]
    public void Session_cookies_are_carried_with_no_expiry()
    {
        var profile = NewDirectory();
        var key = WriteLocalState(profile);
        WriteCookies(profile, key, ("example.com", "tmp", "z", 0, 0, ""));

        var read = ChromiumImportReader.ReadCookies(profile, ChromiumCrypto.Open(profile));

        Assert.Null(Assert.Single(read.Items).ExpiresUtc);
        Assert.Equal(0, read.Skipped);
    }

    [Fact]
    public void App_bound_cookies_are_counted_as_blocked()
    {
        var profile = NewDirectory();
        WriteLocalState(profile, appBound: true);
        var directory = Path.Combine(profile, "Network");
        Directory.CreateDirectory(directory);
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "Cookies")}"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = """
            CREATE TABLE cookies (
                host_key TEXT NOT NULL, top_frame_site_key TEXT NOT NULL DEFAULT '', name TEXT NOT NULL,
                value TEXT NOT NULL, encrypted_value BLOB, path TEXT NOT NULL, expires_utc INTEGER NOT NULL,
                is_secure INTEGER NOT NULL, is_httponly INTEGER NOT NULL, samesite INTEGER NOT NULL,
                is_persistent INTEGER NOT NULL);
            INSERT INTO cookies VALUES ('example.com', '', 'sid', '', x'763230DEADBEEF', '/', 0, 1, 1, 1, 0);
            """;
            create.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var read = ChromiumImportReader.ReadCookies(profile, ChromiumCrypto.Open(profile));

        Assert.Empty(read.Items);
        Assert.Equal(1, read.Blocked);
    }

    // ===== ブックマークの読み取り =====

    [Fact]
    public void Bookmarks_are_read_from_every_root_and_flattened()
    {
        var profile = NewDirectory();
        File.WriteAllText(Path.Combine(profile, "Bookmarks"), """
        {
          "roots": {
            "bookmark_bar": {
              "type": "folder",
              "children": [
                { "type": "url", "name": "例", "url": "https://example.com/", "date_added": "13300000000000000" },
                { "type": "folder", "name": "入れ子", "children": [
                    { "type": "url", "name": "奥", "url": "https://deep.example/x" } ] }
              ]
            },
            "other": {
              "type": "folder",
              "children": [
                { "type": "url", "name": "内部", "url": "chrome://settings" }
              ]
            }
          }
        }
        """);

        var read = ChromiumImportReader.ReadBookmarks(profile);

        // chrome:// は「行き先」ではないので落ちる（BrowserLibrary.IsRecordable）。
        Assert.Equal(2, read.Count);
        Assert.Contains(read.Items, b => b.Url == "https://deep.example/x" && b.Title == "奥");
        Assert.Null(read.Error);
    }

    [Fact]
    public void Bookmarks_keep_the_folder_they_were_filed_in()
    {
        var profile = NewDirectory();
        File.WriteAllText(Path.Combine(profile, "Bookmarks"), """
        {
          "roots": {
            "bookmark_bar": {
              "type": "folder", "name": "ブックマーク バー",
              "children": [
                { "type": "url", "name": "直下", "url": "https://top.example/" },
                { "type": "folder", "name": "開発", "children": [
                    { "type": "url", "name": "奥", "url": "https://deep.example/x" } ] }
              ]
            },
            "other": {
              "type": "folder", "name": "その他のブックマーク",
              "children": [ { "type": "url", "name": "余所", "url": "https://other.example/" } ]
            }
          }
        }
        """);

        var read = ChromiumImportReader.ReadBookmarks(profile);

        // ブックマークバーの子が、こちらの一番上（帯に並ぶ段）。根の名前で段を増やさない。
        Assert.Empty(read.Items.Single(b => b.Url == "https://top.example/").Folder);
        // 入れ子は道が伸びる。潜り終えても他の件に混ざらない（道は複製して渡している）。
        Assert.Equal(new[] { "開発" },
            read.Items.Single(b => b.Url == "https://deep.example/x").Folder);
        // バー以外の決め打ちの入れ物は、名前どおりのフォルダーとして受ける。
        Assert.Equal(new[] { "その他のブックマーク" },
            read.Items.Single(b => b.Url == "https://other.example/").Folder);
    }

    [Fact]
    public void Deleted_bookmarks_in_the_vivaldi_trash_are_not_imported()
    {
        // Vivaldi の trash は「その人が消したブックマーク」。取り込みで蘇らせない。
        var profile = NewDirectory();
        File.WriteAllText(Path.Combine(profile, "Bookmarks"), """
        {
          "roots": {
            "bookmark_bar": {
              "type": "folder", "name": "ブックマーク",
              "children": [ { "type": "url", "name": "現役", "url": "https://live.example/" } ]
            },
            "trash": {
              "type": "folder", "name": "ごみ箱",
              "children": [
                { "type": "url", "name": "捨てた", "url": "https://trashed.example/" },
                { "type": "folder", "name": "捨てた束", "children": [
                    { "type": "url", "name": "捨てた奥", "url": "https://trashed.example/deep" } ] }
              ]
            }
          }
        }
        """);

        var read = ChromiumImportReader.ReadBookmarks(profile);

        Assert.Equal("https://live.example/", Assert.Single(read.Items).Url);
    }

    [Fact]
    public void Missing_bookmark_file_is_empty_not_an_error()
    {
        var read = ChromiumImportReader.ReadBookmarks(NewDirectory());

        Assert.Equal(0, read.Count);
        Assert.Null(read.Error);
    }

    // ===== 併合（BrowserImportMerge） =====

    [Fact]
    public void Imported_bookmarks_do_not_duplicate_what_is_already_there()
    {
        var existing = new List<BrowserBookmark>
        {
            new() { Url = "https://example.com/", Title = "自分で付けた名前" },
        };
        var incoming = new List<BrowserBookmark>
        {
            new() { Url = "https://example.com", Title = "相手の名前" },   // 末尾スラッシュ違いは同じページ
            new() { Url = "https://new.example/", Title = "新顔" },
        };

        var (merged, added) = BrowserImportMerge.Bookmarks(existing, incoming);

        Assert.Equal(1, added);
        Assert.Equal(2, merged.Count);
        Assert.Equal("自分で付けた名前", merged[0].Title);   // 既存の名前は守る
    }

    [Fact]
    public void An_already_bookmarked_page_moves_into_the_folder_it_came_in_with()
    {
        // 取り込む前に ☆ を押していたぶんが一番上に居座ると、持ち込んだ整理に穴が空いて見える。
        var existing = new List<BrowserBookmark> { new() { Url = "https://example.com/", Title = "自分の名前" } };
        var incoming = new List<BrowserBookmark>
        {
            new() { Url = "https://example.com/", Title = "相手の名前", Folder = { "バー", "開発" } },
        };

        var (merged, added) = BrowserImportMerge.Bookmarks(existing, incoming);

        Assert.Equal(0, added);
        Assert.Equal("自分の名前", merged[0].Title);                        // 名前は守ったまま
        Assert.Equal(new[] { "バー", "開発" }, merged[0].Folder);

        // 2回目も同じ結果（置き場所が入っているものは動かさない）。
        var again = BrowserImportMerge.Bookmarks(
            merged, new List<BrowserBookmark> { new() { Url = "https://example.com/", Folder = { "別" } } });
        Assert.Equal(0, again.Added);
        Assert.Equal(new[] { "バー", "開発" }, again.Merged[0].Folder);
    }

    [Fact]
    public void Merged_history_keeps_the_larger_visit_count_and_newer_time()
    {
        var now = DateTime.UtcNow;
        var existing = new List<BrowserHistoryEntry>
        {
            new() { Url = "https://example.com/", VisitCount = 9, LastVisitedUtc = now, Title = "" },
        };
        var incoming = new List<BrowserHistoryEntry>
        {
            new() { Url = "https://example.com/", VisitCount = 2, LastVisitedUtc = now.AddDays(-1), Title = "題" },
        };

        var (merged, added) = BrowserImportMerge.History(existing, incoming, max: 100);

        Assert.Equal(0, added);
        var entry = Assert.Single(merged);
        Assert.Equal(9, entry.VisitCount);
        Assert.Equal(now, entry.LastVisitedUtc);
        Assert.Equal("題", entry.Title);   // 空だったところだけ埋まる
    }

    [Fact]
    public void Merged_history_is_newest_first_and_capped()
    {
        var now = DateTime.UtcNow;
        var incoming = Enumerable.Range(0, 10)
            .Select(i => new BrowserHistoryEntry
            {
                Url = $"https://example.com/{i}",
                LastVisitedUtc = now.AddMinutes(-i),
            })
            .ToList();

        var (merged, added) = BrowserImportMerge.History(new List<BrowserHistoryEntry>(), incoming, max: 3);

        Assert.Equal(10, added);
        Assert.Equal(3, merged.Count);
        Assert.Equal("https://example.com/0", merged[0].Url);
    }

    // ===== CSV（Chrome からの唯一の道） =====

    [Fact]
    public void Csv_reads_columns_by_header_not_by_position()
    {
        var path = Path.Combine(NewDirectory(), "passwords.csv");
        File.WriteAllText(path, "name,username,url,password\n例,taro,https://example.com/login,p@ss\n");

        var read = ChromePasswordCsv.Read(path);

        var item = Assert.Single(read.Items);
        Assert.Equal("taro", item.Username);
        Assert.Equal("p@ss", item.Password);
        // 自動入力の鍵はオリジンまで。パス付きのままだとどのページでも埋まらない行になる。
        Assert.Equal("https://example.com/", item.SignonRealm);
    }

    [Fact]
    public void Csv_survives_quoted_commas_newlines_and_doubled_quotes()
    {
        var path = Path.Combine(NewDirectory(), "passwords.csv");
        File.WriteAllText(path,
            "name,url,username,password,note\n"
            + "\"例, 株式会社\",https://example.com/,taro,\"pa\"\"ss\",\"1行目\n2行目\"\n"
            + "次,https://next.example/,hanako,pw\n");

        var read = ChromePasswordCsv.Read(path);

        Assert.Equal(2, read.Count);
        Assert.Equal("pa\"ss", read.Items[0].Password);
        Assert.Equal("hanako", read.Items[1].Username);
    }

    [Fact]
    public void Csv_with_a_bom_still_matches_its_header()
    {
        var path = Path.Combine(NewDirectory(), "passwords.csv");
        File.WriteAllText(path, "url,username,password\nhttps://example.com/,taro,pw\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var read = ChromePasswordCsv.Read(path);

        Assert.Null(read.Error);
        Assert.Single(read.Items);
    }

    [Fact]
    public void Csv_without_the_expected_columns_says_so()
    {
        var path = Path.Combine(NewDirectory(), "passwords.csv");
        File.WriteAllText(path, "foo,bar\n1,2\n");

        var read = ChromePasswordCsv.Read(path);

        Assert.NotNull(read.Error);
        Assert.Empty(read.Items);
    }

    // ===== 書き込み（LoginDataWriter）＝ 取り込みの終点 =====

    /// <summary>WebView2 と同じ形の（空の）プロファイルを作る。列は実機の <c>Login Data</c> から取った
    /// もので、<b>UNIQUE 制約まで写す</b>——重複除けはこの制約そのものなので、無いと検証にならない。</summary>
    private static string NewWebViewProfile()
    {
        var userData = NewDirectory();
        var profile = Path.Combine(userData, "EBWebView");
        Directory.CreateDirectory(Path.Combine(profile, "Default"));
        WriteLocalState(profile);
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(profile, "Default", "Login Data")}");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
            CREATE TABLE logins (
                origin_url VARCHAR NOT NULL, action_url VARCHAR, username_element VARCHAR,
                username_value VARCHAR, password_element VARCHAR, password_value BLOB,
                submit_element VARCHAR, signon_realm VARCHAR NOT NULL, date_created INTEGER NOT NULL,
                blacklisted_by_user INTEGER NOT NULL, scheme INTEGER NOT NULL, password_type INTEGER,
                times_used INTEGER, form_data BLOB, id INTEGER PRIMARY KEY AUTOINCREMENT,
                date_last_used INTEGER NOT NULL DEFAULT 0,
                date_password_modified INTEGER NOT NULL DEFAULT 0,
                UNIQUE (origin_url, username_element, username_value, password_element, signon_realm))
            """;
            create.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        return userData;
    }

    [Fact]
    public void Written_passwords_can_be_read_back_by_the_pane()
    {
        var userData = NewWebViewProfile();
        var items = new[]
        {
            new ImportedPassword("https://example.com/login", "https://example.com/", "taro", "p@ss", DateTime.UtcNow),
        };

        var result = LoginDataWriter.Write(userData, items);

        Assert.Null(result.Error);
        Assert.Equal(1, result.Added);

        var read = SavedPasswordStore.ForUserDataFolder(userData).Load();
        var saved = Assert.Single(read.Items);
        Assert.Equal("taro", saved.Username);
        Assert.Equal("p@ss", saved.Password);
    }

    [Fact]
    public void Writing_the_same_login_twice_adds_it_once()
    {
        var userData = NewWebViewProfile();
        var items = new[]
        {
            new ImportedPassword("https://example.com/", "https://example.com/", "taro", "p@ss", DateTime.UtcNow),
        };

        Assert.Equal(1, LoginDataWriter.Write(userData, items).Added);
        var second = LoginDataWriter.Write(userData, items);

        Assert.Equal(0, second.Added);
        Assert.Equal(1, second.Skipped);
        Assert.Null(second.Error);
        Assert.Single(SavedPasswordStore.ForUserDataFolder(userData).Load().Items);
    }

    /// <summary>プロファイルがまだ無いときに DB を自作しない（版まで正しく作らないと
    /// Chromium が作り直しに走り、入れた行ごと消える）。</summary>
    [Fact]
    public void Writing_without_a_profile_is_refused_not_improvised()
    {
        var userData = NewDirectory();

        var result = LoginDataWriter.Write(userData, new[]
        {
            new ImportedPassword("https://example.com/", "https://example.com/", "taro", "p@ss", DateTime.UtcNow),
        });

        Assert.NotNull(result.Error);
        Assert.False(File.Exists(Path.Combine(userData, "EBWebView", "Default", "Login Data")));
    }

    // ===== 順番待ち（PendingPasswordImportStore） =====

    [Fact]
    public void Queued_passwords_survive_a_round_trip_and_are_not_stored_in_the_clear()
    {
        var path = Path.Combine(NewDirectory(), "pending.bin");
        var store = new PendingPasswordImportStore(path);
        var items = new[]
        {
            new ImportedPassword("https://example.com/", "https://example.com/", "taro", "ひみつ", DateTime.UtcNow),
        };

        store.Save(items);

        Assert.True(store.HasPending);
        var bytes = File.ReadAllBytes(path);
        Assert.DoesNotContain("ひみつ", Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("example.com", Encoding.UTF8.GetString(bytes));

        var loaded = Assert.Single(store.Load());
        Assert.Equal("ひみつ", loaded.Password);

        store.Clear();
        Assert.False(store.HasPending);
        Assert.Empty(store.Load());
    }

    /// <summary>再起動を挟まずに2回取り込んでも、1回目が消えない。置き換えにしていたときは
    /// 「次回起動時に取り込みます」と出した後で黙って消えていた。</summary>
    [Fact]
    public void Queueing_a_second_source_keeps_the_first()
    {
        var path = Path.Combine(NewDirectory(), "pending.bin");
        var store = new PendingPasswordImportStore(path);

        store.Save(new[]
        {
            new ImportedPassword("https://a.example/", "https://a.example/", "taro", "aaa", DateTime.UtcNow),
        });
        store.Save(new[]
        {
            new ImportedPassword("https://b.example/", "https://b.example/", "hanako", "bbb", DateTime.UtcNow),
        });

        var loaded = store.Load();
        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, p => p.Password == "aaa");
        Assert.Contains(loaded, p => p.Password == "bbb");
    }

    [Fact]
    public void Queueing_the_same_login_twice_keeps_the_newer_one()
    {
        var path = Path.Combine(NewDirectory(), "pending.bin");
        var store = new PendingPasswordImportStore(path);
        var login = new ImportedPassword("https://a.example/", "https://a.example/", "taro", "古い", DateTime.UtcNow);

        store.Save(new[] { login });
        store.Save(new[] { login with { Password = "新しい" } });

        var loaded = Assert.Single(store.Load());
        Assert.Equal("新しい", loaded.Password);
    }

    /// <summary>同一とみなす鍵は書き込み側の <c>UNIQUE</c> と同じ完全一致。大小文字を無視すると、
    /// DB なら別行として入るものをここで先に捨てることになる。</summary>
    [Fact]
    public void Queue_treats_case_differences_as_distinct_like_the_database_does()
    {
        var merged = PendingPasswordImportStore.Merge(
            new[] { new ImportedPassword("https://a.example/", "https://a.example/", "Taro", "x", DateTime.UtcNow) },
            new[] { new ImportedPassword("https://a.example/", "https://a.example/", "taro", "y", DateTime.UtcNow) });

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void A_corrupt_queue_file_is_empty_rather_than_fatal()
    {
        var path = Path.Combine(NewDirectory(), "pending.bin");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });

        Assert.Empty(new PendingPasswordImportStore(path).Load());
    }

    // ===== 取り込み元の検出 =====

    [Fact]
    public void A_folder_without_any_data_is_not_a_profile()
    {
        var empty = NewDirectory();
        var real = NewDirectory();
        File.WriteAllText(Path.Combine(real, "Bookmarks"), "{}");

        Assert.False(ChromiumImportReader.HasAnyData(empty));
        Assert.True(ChromiumImportReader.HasAnyData(real));
    }

    [Fact]
    public void Profile_names_come_from_local_state_and_fall_back_to_the_folder()
    {
        var userData = NewDirectory();
        Directory.CreateDirectory(Path.Combine(userData, "Default"));
        Directory.CreateDirectory(Path.Combine(userData, "Profile 1"));
        Directory.CreateDirectory(Path.Combine(userData, "System Profile"));
        foreach (var folder in new[] { "Default", "Profile 1", "System Profile" })
            File.WriteAllText(Path.Combine(userData, folder, "Bookmarks"), "{}");
        File.WriteAllText(Path.Combine(userData, "Local State"), JsonSerializer.Serialize(new
        {
            profile = new { info_cache = new Dictionary<string, object> { ["Default"] = new { name = "仕事" } } },
        }));

        var profiles = ChromiumBrowsers.ProfilesOf(new ChromiumBrowser("試験", userData, new[] { "nothing" }));

        Assert.Equal(2, profiles.Count);   // System Profile は候補にしない
        Assert.Equal("試験 · 仕事", profiles[0].Label);
        Assert.Equal("試験 · Profile 1", profiles[1].Label);
    }
}
