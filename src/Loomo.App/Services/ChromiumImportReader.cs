using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;

namespace sk0ya.Loomo.App.Services;

/// <summary>取り込む Cookie 1件（＝ログインしたままの状態の正体）。</summary>
public sealed record ImportedCookie(
    string Name, string Value, string Domain, string Path,
    DateTime? ExpiresUtc, bool IsSecure, bool IsHttpOnly, int SameSite);

/// <summary>取り込むログイン情報1件。</summary>
public sealed record ImportedPassword(
    string Origin, string SignonRealm, string Username, string Password, DateTime CreatedUtc);

/// <summary>1種類ぶんの読み取り結果。<b>読めた数と読めなかった数を両方持つ</b>——
/// アプリ束縛暗号で解けなかった項目を黙って落とすと「全部移った」と誤解させるため。</summary>
public sealed record ImportRead<T>(IReadOnlyList<T> Items, int Blocked, string? Error)
{
    /// <summary>解けなかったのではなく、<b>持ち込むべきでないと判断して</b>外したぶん
    /// （区画付き Cookie など）。理由が違うので <see cref="Blocked"/> と混ぜない。</summary>
    public int Skipped { get; init; }

    public static ImportRead<T> Empty(string? error = null) => new(Array.Empty<T>(), 0, error);
    public int Count => Items.Count;
}

/// <summary>
/// 他所の Chromium 系プロファイルから、ブックマーク・履歴・ログイン情報・Cookie を読む（設計書 §21.5.4）。
///
/// <para><b>相手の実体には触らない</b>。SQLite は必ず一時コピー（<see cref="ChromiumDatabaseCopy"/>）を読み、
/// 書き戻しは一切しない。取り込みの失敗で<b>移行元が壊れる</b>のが最悪の結果なので、そこだけは形で保証する。</para>
///
/// <para><b>種類ごとに条件が違う</b>のがこの機能の勘所で、実測に基づいて分けてある：
/// ブックマーク（JSON）と履歴（SQLite）は<b>暗号化されておらず</b>相手が起動中でも読める。
/// ログイン情報は暗号化されているが、稼働中でもコピーは取れる。
/// <b>Cookie だけは相手を完全に終了させないと読めない</b>——稼働中の <c>Network/Cookies</c> は
/// 共有違反で開けない（Chrome も Vivaldi も、そして自分の WebView2 も同じ）。
/// さらに Chrome 127 以降のアプリ束縛暗号（<c>v20</c>）が掛かった項目は<b>原理的にこちらでは解けない</b>ので、
/// 数だけ数えて <see cref="ImportRead{T}.Blocked"/> で持ち帰り、画面で正直に伝える。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class ChromiumImportReader
{
    /// <summary>プロファイルらしさの判定（<see cref="ChromiumBrowsers"/> が一覧を組むのに使う）。</summary>
    public static bool HasAnyData(string profilePath)
        => File.Exists(Path.Combine(profilePath, "Bookmarks"))
            || File.Exists(Path.Combine(profilePath, "History"))
            || File.Exists(Path.Combine(profilePath, "Login Data"));

    /// <summary>Cookie の置き場所。M84 あたりで <c>Network/</c> の下へ移ったので、古い位置も見る。</summary>
    public static string CookiesPath(string profilePath)
    {
        var moved = Path.Combine(profilePath, "Network", "Cookies");
        return File.Exists(moved) ? moved : Path.Combine(profilePath, "Cookies");
    }

    /// <summary>Cookie を読むには相手が終了している必要がある。<b>読む前に確かめて促す</b>ためのもの。
    /// プロセス名ではなく<b>実際にファイルを開いて</b>確かめる——「Vivaldi は終了したのに、常駐設定で
    /// バックグラウンドに残っている」を見抜けるのはこちらだけ。</summary>
    public static bool IsCookieDatabaseLocked(string profilePath)
    {
        var path = CookiesPath(profilePath);
        if (!File.Exists(path))
            return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    // ── ブックマーク ───────────────────────────────────────────────────
    /// <summary><c>Bookmarks</c>（JSON）を読む。<b>フォルダーの階層はそのまま持ち込む</b>——
    /// 「ブックマーク バー／開発」のような置き場所は、その人が自分で作った整理そのもので、
    /// 平らにすると数百件が一列になって使い物にならない。木に組み直すのは表示側
    /// （<see cref="BrowserBookmarkTree"/>）で、ここは1件ごとに道
    /// （<see cref="BrowserBookmark.Folder"/>）を付けて返すだけ。
    /// <c>roots</c> の直下（「ブックマーク バー」「その他のブックマーク」）も普通のフォルダーとして数える。</summary>
    public static ImportRead<BrowserBookmark> ReadBookmarks(string profilePath)
    {
        var path = Path.Combine(profilePath, "Bookmarks");
        if (!File.Exists(path))
            return ImportRead<BrowserBookmark>.Empty();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("roots", out var roots)
                || roots.ValueKind != JsonValueKind.Object)
                return ImportRead<BrowserBookmark>.Empty();
            var items = new List<BrowserBookmark>();
            foreach (var root in roots.EnumerateObject())
                CollectBookmarks(root.Value, items, new List<string>());
            return new ImportRead<BrowserBookmark>(items, 0, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return ImportRead<BrowserBookmark>.Empty($"ブックマークを読めませんでした: {ex.Message}");
        }
    }

    /// <param name="folder">いま潜っているフォルダーの道（根から数えた名前の並び）。</param>
    private static void CollectBookmarks(JsonElement node, List<BrowserBookmark> items, List<string> folder)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return;
        var type = node.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;
        var nodeName = node.TryGetProperty("name", out var name) ? name.GetString() : null;
        if (type == "url")
        {
            if (!node.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String)
                return;
            var value = url.GetString();
            if (!BrowserLibrary.IsRecordable(value))
                return;
            items.Add(new BrowserBookmark
            {
                Url = value!,
                Title = nodeName,
                AddedUtc = ReadJsonChromiumTime(node, "date_added"),
                // 道は<b>複製して</b>渡す（潜り終えて縮む同じ実体を渡すと、全件が最後の道を指す）。
                Folder = new List<string>(folder),
            });
            return;
        }
        if (!node.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array)
            return;
        // 名前の無いフォルダーでは段を増やさない（表示できない空の段ができるだけ）。
        var named = !string.IsNullOrWhiteSpace(nodeName);
        if (named)
            folder.Add(nodeName!.Trim());
        foreach (var child in children.EnumerateArray())
            CollectBookmarks(child, items, folder);
        if (named)
            folder.RemoveAt(folder.Count - 1);
    }

    /// <summary>Bookmarks の日時は<b>文字列で入った</b> Chromium 時刻。無い／壊れていれば「いま」にする
    /// （並びの都合の値で、これ1件のために取り込みを止める意味が無い）。</summary>
    private static DateTime ReadJsonChromiumTime(JsonElement node, string property)
    {
        if (!node.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String
            || !long.TryParse(value.GetString(), out var micro))
            return DateTime.UtcNow;
        var parsed = ChromiumCrypto.FromChromiumTime(micro);
        return parsed == DateTime.MinValue ? DateTime.UtcNow : parsed;
    }

    // ── 履歴 ───────────────────────────────────────────────────────────
    /// <summary><c>History</c> の <c>urls</c> を新しい順に読む。<b>件数を絞って読む</b>——
    /// 何年ぶんも溜まった履歴をそのまま持ち込むと <c>browser.json</c>（全件を毎回書き直す作り）が重くなり、
    /// 候補の質も上がらない。</summary>
    public static ImportRead<BrowserHistoryEntry> ReadHistory(string profilePath, int limit)
    {
        var path = Path.Combine(profilePath, "History");
        if (!File.Exists(path))
            return ImportRead<BrowserHistoryEntry>.Empty();
        var working = ChromiumDatabaseCopy.NewWorkingDirectory("history");
        try
        {
            var database = ChromiumDatabaseCopy.To(path, working);
            var items = new List<BrowserHistoryEntry>();
            using var connection = OpenCopy(database);
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT url, title, visit_count, last_visit_time FROM urls "
                + "WHERE hidden = 0 ORDER BY last_visit_time DESC LIMIT $limit";
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var url = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (!BrowserLibrary.IsRecordable(url))
                    continue;
                items.Add(new BrowserHistoryEntry
                {
                    Url = url!,
                    Title = reader.IsDBNull(1) ? null : reader.GetString(1),
                    VisitCount = reader.IsDBNull(2) ? 1 : Math.Max(1, reader.GetInt32(2)),
                    LastVisitedUtc = ChromiumCrypto.FromChromiumTime(reader.IsDBNull(3) ? 0 : reader.GetInt64(3)),
                });
            }
            return new ImportRead<BrowserHistoryEntry>(items, 0, null);
        }
        catch (Exception ex) when (ex is IOException or SqliteException or UnauthorizedAccessException)
        {
            return ImportRead<BrowserHistoryEntry>.Empty($"履歴を読めませんでした: {ex.Message}");
        }
        finally
        {
            ChromiumDatabaseCopy.TryDelete(working);
        }
    }

    // ── ログイン情報 ───────────────────────────────────────────────────
    /// <summary><c>Login Data</c> を読む。相手が起動中でも読める（実測）。</summary>
    public static ImportRead<ImportedPassword> ReadPasswords(string profilePath, ChromiumCrypto crypto)
    {
        var path = Path.Combine(profilePath, "Login Data");
        if (!File.Exists(path))
            return ImportRead<ImportedPassword>.Empty();
        var working = ChromiumDatabaseCopy.NewWorkingDirectory("import-logins");
        try
        {
            var database = ChromiumDatabaseCopy.To(path, working);
            var items = new List<ImportedPassword>();
            var blocked = 0;
            using var connection = OpenCopy(database);
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT origin_url, signon_realm, username_value, password_value, date_created "
                + "FROM logins WHERE blacklisted_by_user = 0 ORDER BY origin_url";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    var blob = reader.IsDBNull(3) ? Array.Empty<byte>() : (byte[])reader[3];
                    if (blob.Length == 0)
                        continue;   // 「保存しない」を選んだサイトは空で入っている
                    if (ChromiumCrypto.IsAppBoundValue(blob) || crypto.TryDecryptText(blob) is not { Length: > 0 } password)
                    {
                        blocked++;
                        continue;
                    }
                    var origin = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    var realm = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    if (origin.Length == 0 && realm.Length == 0)
                        continue;
                    items.Add(new ImportedPassword(
                        origin.Length == 0 ? realm : origin,
                        realm.Length == 0 ? origin : realm,
                        reader.IsDBNull(2) ? "" : reader.GetString(2),
                        password,
                        ChromiumCrypto.FromChromiumTime(reader.IsDBNull(4) ? 0 : reader.GetInt64(4))));
                }
                catch (Exception ex) when (ex is InvalidCastException or FormatException)
                {
                }
            }
            return new ImportRead<ImportedPassword>(items, blocked, null);
        }
        catch (Exception ex) when (ex is IOException or SqliteException or UnauthorizedAccessException)
        {
            return ImportRead<ImportedPassword>.Empty($"ログイン情報を読めませんでした: {ex.Message}");
        }
        finally
        {
            ChromiumDatabaseCopy.TryDelete(working);
        }
    }

    // ── Cookie ─────────────────────────────────────────────────────────
    /// <summary><c>Network/Cookies</c> を読む。<b>相手が動いていると開けない</b>ので、
    /// 呼ぶ前に <see cref="IsCookieDatabaseLocked"/> で確かめて促すこと。</summary>
    public static ImportRead<ImportedCookie> ReadCookies(string profilePath, ChromiumCrypto crypto)
    {
        var path = CookiesPath(profilePath);
        if (!File.Exists(path))
            return ImportRead<ImportedCookie>.Empty();
        var working = ChromiumDatabaseCopy.NewWorkingDirectory("cookies");
        try
        {
            var database = ChromiumDatabaseCopy.To(path, working);
            var items = new List<ImportedCookie>();
            var blocked = 0;
            var skipped = 0;
            using var connection = OpenCopy(database);
            // 列は Chromium のバージョンで増減する。無い列を SELECT すると<b>全体が</b>落ちるので、
            // あるものだけを組み立てて読む（samesite / is_persistent / top_frame_site_key は無い版がある）。
            var columns = TableColumns(connection, "cookies");
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT host_key, name, value, encrypted_value, path, expires_utc, is_secure, is_httponly"
                + (columns.Contains("samesite") ? ", samesite" : ", -1")
                + (columns.Contains("is_persistent") ? ", is_persistent"
                    : columns.Contains("has_expires") ? ", has_expires" : ", 1")
                + (columns.Contains("top_frame_site_key") ? ", top_frame_site_key" : ", ''")
                + " FROM cookies";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    var host = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    if (host.Length == 0)
                        continue;
                    // 区画付き Cookie（CHIPS）は「どのサイトに埋め込まれているときの Cookie か」まで
                    // 含めて1件で、WebView2 の CookieManager には区画を指定する入口が無い。
                    // 区画を落として入れると<b>本来通用しない場所で通る</b> Cookie ができる
                    // （第三者の Cookie が第一者の顔をする）ので、入れずに数えて伝える。
                    if (!reader.IsDBNull(10) && reader.GetString(10).Length > 0)
                    {
                        skipped++;
                        continue;
                    }
                    var blob = reader.IsDBNull(3) ? Array.Empty<byte>() : (byte[])reader[3];
                    string? value;
                    if (blob.Length == 0)
                    {
                        // 暗号化される前の古い項目は value にそのまま入っている。
                        value = reader.IsDBNull(2) ? null : reader.GetString(2);
                    }
                    else if (ChromiumCrypto.IsAppBoundValue(blob))
                    {
                        blocked++;
                        continue;
                    }
                    else if ((value = crypto.TryDecryptCookie(blob, host)) is null)
                    {
                        blocked++;
                        continue;
                    }
                    if (value is null)
                        continue;
                    var persistent = !reader.IsDBNull(9) && reader.GetInt64(9) != 0;
                    var expires = ChromiumCrypto.FromChromiumTime(reader.IsDBNull(5) ? 0 : reader.GetInt64(5));
                    // 期限切れは渡しても相手（Chromium）が黙って捨てる。渡す前に外しておかないと
                    // 「N 件取り込みました」の N が<b>実際に入った数と食い違う</b>。
                    if (persistent && expires <= DateTime.UtcNow)
                    {
                        skipped++;
                        continue;
                    }
                    items.Add(new ImportedCookie(
                        reader.IsDBNull(1) ? "" : reader.GetString(1),
                        value,
                        host,
                        reader.IsDBNull(4) ? "/" : reader.GetString(4),
                        persistent && expires > DateTime.UnixEpoch ? expires : null,
                        !reader.IsDBNull(6) && reader.GetInt64(6) != 0,
                        !reader.IsDBNull(7) && reader.GetInt64(7) != 0,
                        reader.IsDBNull(8) ? -1 : (int)reader.GetInt64(8)));
                }
                catch (Exception ex) when (ex is InvalidCastException or FormatException)
                {
                }
            }
            return new ImportRead<ImportedCookie>(items, blocked, null) { Skipped = skipped };
        }
        catch (Exception ex) when (ex is IOException or SqliteException or UnauthorizedAccessException)
        {
            return ImportRead<ImportedCookie>.Empty(
                $"Cookie を読めませんでした（ブラウザを終了してから試してください）: {ex.Message}");
        }
        finally
        {
            ChromiumDatabaseCopy.TryDelete(working);
        }
    }

    private static HashSet<string> TableColumns(SqliteConnection connection, string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));
        return columns;
    }

    /// <summary>コピーを開く。Pooling=false は必須（掴んだままだと一時フォルダーを消せず、
    /// 資格情報や Cookie のコピーが %TEMP% に溜まり続ける）。</summary>
    private static SqliteConnection OpenCopy(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,   // WAL を畳むのに書き込みが要る（相手ではなくコピー）
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}
