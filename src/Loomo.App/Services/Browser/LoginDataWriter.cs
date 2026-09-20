using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;

namespace sk0ya.Loomo.App.Services;

/// <summary>書き込みの結果。<b>入れた数と重複で飛ばした数を分ける</b>——
/// 二度目の取り込みで「0件」と出たとき、失敗なのか既にあるのかが分からないと判断できない。</summary>
public sealed record LoginWriteResult(int Added, int Skipped, string? Error)
{
    public static LoginWriteResult Failed(string message) => new(0, 0, message);
    public bool IsSuccess => Error is null;
}

/// <summary>
/// 取り込んだログイン情報を、こちらの WebView2 プロファイルの <c>Login Data</c> へ書く（設計書 §21.5.4）。
///
/// <para><b>ここだけが Login Data に書き込む</b>。<see cref="SavedPasswordStore"/> が読み専門なのは
/// 「稼働中のブラウザが掴んでいる DB を書き換えるとプロファイルを壊す」からで、その禁を破るのではなく
/// <b>誰も掴んでいない時間に書く</b>ことで両立させている——具体的には
/// <see cref="PendingPasswordImportStore"/> に積んでおき、WebView2 がまだ1つも作られていない
/// <b>起動直後</b>に <see cref="ApplyPending"/> が流し込む。WebView2 が動いている最中に呼べば
/// SQLite が <c>database is locked</c> を返すので、壊れるのではなく<b>断られる</b>のもここの設計。</para>
///
/// <para>入れ方は Chromium が取り込み時にするのと同じで、要素名（<c>username_element</c> /
/// <c>password_element</c>）は空にする。この2つは <c>UNIQUE (origin_url, username_element,
/// username_value, password_element, signon_realm)</c> の一部なので、空で揃えることが
/// 「同じサイトの同じユーザーは1件」という重複除けそのものになる（<c>INSERT OR IGNORE</c>）。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class LoginDataWriter
{
    /// <summary>WebView2 が動いていると書けない。書く前に確かめて、無駄に例外を出さないため。</summary>
    public static bool IsWritable(string userDataFolder)
    {
        var path = LoginDataPath(userDataFolder);
        if (!File.Exists(path))
            return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string LoginDataPath(string userDataFolder)
        => Path.Combine(userDataFolder, "EBWebView", "Default", "Login Data");

    /// <summary>積んであるぶんを流し込んで、成功したら控えを消す。<b>起動直後に一度だけ</b>呼ぶ。
    /// 失敗しても控えは残す——WebView2 が生きていて書けなかっただけかもしれず、
    /// そこで捨てると利用者は取り込みをやり直す羽目になる。</summary>
    public static LoginWriteResult ApplyPending(string userDataFolder)
    {
        var store = new PendingPasswordImportStore();
        if (store.Load() is not { Count: > 0 } pending)
            return new LoginWriteResult(0, 0, null);
        var result = Write(userDataFolder, pending);
        if (result.IsSuccess)
            store.Clear();
        return result;
    }

    /// <summary>いま書く。<c>Login Data</c> が無い（＝ブラウザペインを一度も開いていない）ときは、
    /// <b>DB を自分で作らない</b>——スキーマの版まで正しく作らないと Chromium 側が作り直しに走り、
    /// せっかく入れた行ごと消える。素直に断って、一度開いてもらう。</summary>
    public static LoginWriteResult Write(string userDataFolder, IReadOnlyList<ImportedPassword> items)
    {
        if (items.Count == 0)
            return new LoginWriteResult(0, 0, null);
        var path = LoginDataPath(userDataFolder);
        if (!File.Exists(path))
            return LoginWriteResult.Failed("ブラウザペインを一度開いてから取り込んでください。");
        if (!ChromiumCrypto.TryOpen(Path.Combine(userDataFolder, "EBWebView"), out var crypto, out var error))
            return LoginWriteResult.Failed(error!);

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,   // 掴んだままにすると、この後 WebView2 が開けなくなる
            }.ToString());
            connection.Open();
            using (var busy = connection.CreateCommand())
            {
                // 掴まれていたら粘らずに諦める（起動を待たせない。次の機会に積み直せばよい）。
                busy.CommandText = "PRAGMA busy_timeout = 2000";
                busy.ExecuteNonQuery();
            }
            var added = 0;
            using var transaction = connection.BeginTransaction();
            foreach (var item in items)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT OR IGNORE INTO logins ("
                    + "origin_url, action_url, username_element, username_value, password_element, password_value, "
                    + "submit_element, signon_realm, date_created, blacklisted_by_user, scheme, password_type, "
                    + "times_used, date_last_used, date_password_modified) "
                    + "VALUES ($origin, $origin, '', $user, '', $password, "
                    + "'', $realm, $created, 0, 0, 0, 0, 0, $created)";
                insert.Parameters.AddWithValue("$origin", item.Origin);
                insert.Parameters.AddWithValue("$user", item.Username);
                insert.Parameters.AddWithValue("$password", crypto!.EncryptV10(item.Password));
                insert.Parameters.AddWithValue("$realm", item.SignonRealm);
                insert.Parameters.AddWithValue("$created", ChromiumCrypto.ToChromiumTime(
                    item.CreatedUtc == DateTime.MinValue ? DateTime.UtcNow : item.CreatedUtc));
                added += insert.ExecuteNonQuery();
            }
            transaction.Commit();
            return new LoginWriteResult(added, items.Count - added, null);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)   // BUSY / LOCKED
        {
            return LoginWriteResult.Failed(
                "ブラウザが使用中のため書き込めませんでした（Loomo を再起動すると取り込みます）。");
        }
        catch (Exception ex) when (ex is IOException or SqliteException or UnauthorizedAccessException)
        {
            return LoginWriteResult.Failed($"ログイン情報を書き込めませんでした: {ex.Message}");
        }
    }
}
