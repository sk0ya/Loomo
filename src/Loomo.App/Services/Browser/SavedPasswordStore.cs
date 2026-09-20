using System.Runtime.Versioning;
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
/// Chromium 系プロファイルの <c>Login Data</c> に保存されたログイン情報を読む。
///
/// <para><b>なぜ自前で読むのか</b>：ブラウザペインは <c>IsPasswordAutosaveEnabled</c> を立てているので
/// 保存そのものは Edge が行うが、WebView2 では <c>edge://settings/passwords</c> が開けない——
/// つまり<b>保存はされるのに二度と見られない</b>。ここが埋まらないと「パスワードを覚えさせる」判断ができない。</para>
///
/// <para><b>読み方</b>は <see cref="ChromiumCrypto"/> に寄せた（鍵と v10 の綴りはあちらが唯一の持ち主）。
/// 構造が同じなので、<c>profileRoot</c> を差し替えれば<b>他所のブラウザ</b>（Vivaldi など）も同じ手で読める
/// ——取り込み（<see cref="ChromiumImportReader"/>）はそれを利用している。</para>
///
/// <para><b>ここでは書き込まない</b>。Login Data はブラウザが開いている間ずっと掴んでいるので、
/// 消したり書き換えたりするのはプロファイルを壊す道になる。読むときも実体には触らず一時コピーを開く。
/// 一括削除だけは WebView2 の <c>ClearBrowsingDataAsync</c>（＝ブラウザ自身にやらせる）を使い、
/// 取り込みの書き込みは<b>WebView2 が動いていない起動直後</b>に <see cref="LoginDataWriter"/> が行う。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SavedPasswordStore(string profileRoot)
{
    /// <summary>WebView2 は UserDataFolder の下に <c>EBWebView</c> を作り、その中がプロファイル一式。</summary>
    public static SavedPasswordStore ForUserDataFolder(string userDataFolder)
        => new(Path.Combine(userDataFolder, "EBWebView"));

    private string LoginDataPath => Path.Combine(profileRoot, "Default", "Login Data");

    /// <summary>まだ一度もブラウザを開いていない（プロファイルが無い）ときは、一覧そのものを出さない。</summary>
    public bool IsAvailable => File.Exists(LoginDataPath);

    public SavedPasswordResult Load()
    {
        if (!IsAvailable)
            return SavedPasswordResult.Failed("プロファイルがまだありません。");
        if (!ChromiumCrypto.TryOpen(profileRoot, out var crypto, out var error))
            return SavedPasswordResult.Failed(error!);

        // 後始末の対象はコピー先の<b>フォルダー</b>を先に決めておく。コピーが途中で投げると
        // 戻り値は受け取れないが、そのときには既に Login Data を書き終えていることがある。
        var workingDirectory = ChromiumDatabaseCopy.NewWorkingDirectory("logins");
        try
        {
            var database = ChromiumDatabaseCopy.To(LoginDataPath, workingDirectory);
            return new SavedPasswordResult(ReadLogins(database, crypto!), null);
        }
        catch (Exception ex) when (ex is IOException or SqliteException or UnauthorizedAccessException)
        {
            return SavedPasswordResult.Failed($"保存済みの情報を読めませんでした: {ex.Message}");
        }
        finally
        {
            ChromiumDatabaseCopy.TryDelete(workingDirectory);
        }
    }

    internal static List<SavedPassword> ReadLogins(string databasePath, ChromiumCrypto crypto)
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
                if (crypto.TryDecryptText(blob) is not { } password)
                    continue;
                items.Add(new SavedPassword(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    password,
                    ChromiumCrypto.FromChromiumTime(reader.IsDBNull(3) ? 0 : reader.GetInt64(3))));
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException)
            {
            }
        }
        return items;
    }
}
