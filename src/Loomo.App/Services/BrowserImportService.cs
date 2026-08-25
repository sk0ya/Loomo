using System.Runtime.Versioning;

namespace sk0ya.Loomo.App.Services;

/// <summary>何を取り込むか。種類ごとに条件（相手の終了が要る／解けない項目がある）が違うので、
/// 一括ではなく種類単位で選ばせる。</summary>
public sealed record BrowserImportSelection(bool Bookmarks, bool History, bool Passwords, bool Cookies)
{
    public bool IsEmpty => !Bookmarks && !History && !Passwords && !Cookies;
}

/// <summary>読み出した中身と、読めなかったぶんの内訳。<b>持ち帰るのは事実だけ</b>で、
/// どこへ入れるかの判断は呼び出し側（ブックマーク／履歴は VM、Cookie はシェル）が持つ。</summary>
public sealed record BrowserImportHarvest(
    IReadOnlyList<BrowserBookmark> Bookmarks,
    IReadOnlyList<BrowserHistoryEntry> History,
    IReadOnlyList<ImportedPassword> Passwords,
    IReadOnlyList<ImportedCookie> Cookies,
    int BlockedPasswords,
    int BlockedCookies,
    IReadOnlyList<string> Errors)
{
    /// <summary>区画付き（CHIPS）で持ち込まなかった Cookie の数。解けなかったのとは理由が違う。</summary>
    public int SkippedCookies { get; init; }

    public static BrowserImportHarvest Failed(string error) => new(
        Array.Empty<BrowserBookmark>(), Array.Empty<BrowserHistoryEntry>(),
        Array.Empty<ImportedPassword>(), Array.Empty<ImportedCookie>(), 0, 0, new[] { error });

    /// <summary>解けなかった項目の合計（アプリ束縛暗号など）。0 でなければ画面で必ず触れる。</summary>
    public int Blocked => BlockedPasswords + BlockedCookies;
}

/// <summary>
/// 他所のブラウザから中身を読み出す入口（設計書 §21.5.4）。
///
/// <para>ここは<b>読むだけ</b>で、どこへも書かない。書き先が3つに割れている
/// （ブックマーク／履歴＝<c>browser.json</c>、Cookie＝WebView2 の CookieManager、
/// パスワード＝次の起動での <c>Login Data</c> 書き込み）ためで、それぞれ持ち主が違う。
/// 唯一の例外が <see cref="QueuePasswords"/>——あれは「書く」のではなく
/// <b>書く順番待ちに積む</b>だけなので、読み出しと同じ流れで済ませてよい。</para>
///
/// <para><b>キャッシュは扱わない</b>。HTTP キャッシュは持ち込んでも当たらないことが多く
/// （検証情報もバージョンも相手のものなので）、プロファイルを壊す危険だけが残る。
/// 「ログインしたままの状態を移したい」という要求の実体は<b>Cookie</b> なので、そちらで満たす。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class BrowserImportService
{
    /// <summary>持ち込む履歴の上限。<c>browser.json</c> は全件を毎回書き直す作りなので、
    /// 際限なく持ち込むと保存のたびに重くなる（候補の質に効くのは直近ぶんだけ）。</summary>
    public const int HistoryLimit = 500;

    public static BrowserImportHarvest Harvest(ChromiumProfileRef profile, BrowserImportSelection selection)
    {
        var errors = new List<string>();
        var bookmarks = Array.Empty<BrowserBookmark>() as IReadOnlyList<BrowserBookmark>;
        var history = Array.Empty<BrowserHistoryEntry>() as IReadOnlyList<BrowserHistoryEntry>;
        var passwords = Array.Empty<ImportedPassword>() as IReadOnlyList<ImportedPassword>;
        var cookies = Array.Empty<ImportedCookie>() as IReadOnlyList<ImportedCookie>;
        var blockedPasswords = 0;
        var blockedCookies = 0;
        var skippedCookies = 0;

        if (selection.Bookmarks)
        {
            var read = ChromiumImportReader.ReadBookmarks(profile.Path);
            bookmarks = read.Items;
            AddError(errors, read.Error);
        }
        if (selection.History)
        {
            var read = ChromiumImportReader.ReadHistory(profile.Path, HistoryLimit);
            history = read.Items;
            AddError(errors, read.Error);
        }

        // 暗号鍵は パスワードと Cookie で共通なので、要るときだけ一度だけ開ける。
        if (selection.Passwords || selection.Cookies)
        {
            if (!ChromiumCrypto.TryOpen(profile.Browser.UserDataFolder, out var crypto, out var error))
            {
                AddError(errors, error);
            }
            else
            {
                if (selection.Passwords)
                {
                    var read = ChromiumImportReader.ReadPasswords(profile.Path, crypto!);
                    passwords = read.Items;
                    blockedPasswords = read.Blocked;
                    AddError(errors, read.Error);
                }
                if (selection.Cookies)
                {
                    if (ChromiumImportReader.IsCookieDatabaseLocked(profile.Path))
                    {
                        errors.Add($"{profile.Browser.DisplayName} を完全に終了してから Cookie を取り込んでください。");
                    }
                    else
                    {
                        var read = ChromiumImportReader.ReadCookies(profile.Path, crypto!);
                        cookies = read.Items;
                        blockedCookies = read.Blocked;
                        skippedCookies = read.Skipped;
                        AddError(errors, read.Error);
                    }
                }
            }
        }

        return new BrowserImportHarvest(
            bookmarks, history, passwords, cookies, blockedPasswords, blockedCookies, errors)
        {
            SkippedCookies = skippedCookies,
        };
    }

    /// <summary>パスワードを次の起動での書き込みに積む。<b>その場では書けない</b>——
    /// <c>Login Data</c> は稼働中の WebView2 が掴んでいるため（<see cref="LoginDataWriter"/>）。</summary>
    public static int QueuePasswords(IReadOnlyList<ImportedPassword> passwords)
    {
        if (passwords.Count == 0)
            return 0;
        new PendingPasswordImportStore().Save(passwords);
        return passwords.Count;
    }

    private static void AddError(List<string> errors, string? error)
    {
        if (!string.IsNullOrEmpty(error))
            errors.Add(error!);
    }
}

/// <summary>
/// 取り込んだブックマーク・履歴を、いま持っているものへ<b>混ぜる</b>規則（純関数）。
///
/// <para><b>置き換えではなく併合</b>にするのがここの肝。取り込みは「引っ越し」ではなく
/// 「持ち込み」で、Loomo で溜めたものが消えるのは受け入れられない。同じ URL がぶつかったときは
/// <b>多いほう・新しいほう</b>を採る——訪問回数を持ち込みの 1 で上書きすると、
/// アドレス欄の候補順（訪問回数で並ぶ）が静かに劣化する。</para>
/// </summary>
public static class BrowserImportMerge
{
    /// <summary>ブックマークを併合して、増えた件数と一緒に返す。既にあるものには触らない
    /// （タイトルを相手のもので上書きすると、自分で付け直した名前が消える）。</summary>
    public static (List<BrowserBookmark> Merged, int Added) Bookmarks(
        IEnumerable<BrowserBookmark> existing, IEnumerable<BrowserBookmark> incoming)
    {
        var merged = existing.ToList();
        var seen = new HashSet<string>(
            merged.Select(b => BrowserLibrary.Normalize(b.Url)), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var bookmark in incoming)
        {
            if (!BrowserLibrary.IsRecordable(bookmark.Url) || !seen.Add(BrowserLibrary.Normalize(bookmark.Url)))
                continue;
            merged.Add(bookmark);
            added++;
        }
        return (merged, added);
    }

    /// <summary>履歴を併合して、新しい順に整えたものを返す（上限は呼び出し側の
    /// <see cref="BrowserLibraryStore.MaxHistory"/> で切る）。</summary>
    public static (List<BrowserHistoryEntry> Merged, int Added) History(
        IEnumerable<BrowserHistoryEntry> existing, IEnumerable<BrowserHistoryEntry> incoming, int max)
    {
        var merged = existing.ToList();
        var index = new Dictionary<string, BrowserHistoryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in merged)
            index.TryAdd(BrowserLibrary.Normalize(entry.Url), entry);

        var added = 0;
        foreach (var entry in incoming)
        {
            if (!BrowserLibrary.IsRecordable(entry.Url))
                continue;
            var key = BrowserLibrary.Normalize(entry.Url);
            if (index.TryGetValue(key, out var found))
            {
                found.VisitCount = Math.Max(found.VisitCount, entry.VisitCount);
                if (entry.LastVisitedUtc > found.LastVisitedUtc)
                    found.LastVisitedUtc = entry.LastVisitedUtc;
                if (string.IsNullOrWhiteSpace(found.Title) && !string.IsNullOrWhiteSpace(entry.Title))
                    found.Title = entry.Title;
                continue;
            }
            index[key] = entry;
            merged.Add(entry);
            added++;
        }
        var ordered = merged.OrderByDescending(e => e.LastVisitedUtc).ToList();
        return (BrowserLibrary.Trim(ordered, max), added);
    }
}
