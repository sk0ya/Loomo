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

    /// <summary>積む。既にある待ちには<b>足す</b>——<b>置き換えてはいけない</b>。
    /// 再起動を挟まずに2回取り込む（Vivaldi から取り込んだ後、続けて Chrome の CSV を読む）のは
    /// 普通の流れで、置き換えるとそこで1回目が黙って消える——しかも画面には
    /// 「次回起動時に取り込みます」と出た後なので、消えたことに気づく手がかりが無い。
    /// 同じ資格情報が二度来たら<b>後から来たほうを採る</b>（積み上がりはここで止める）。</summary>
    public void Save(IReadOnlyList<ImportedPassword> items)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.SerializeToUtf8Bytes(Merge(Load(), items));
            File.WriteAllBytes(_path, ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // 積めなくてもブラウズは続けられる。次の取り込みでやり直せる。
        }
    }

    /// <summary>待ちを足し合わせる。<b>同一とみなす鍵は書き込み側の <c>UNIQUE</c> と同じ</b>——
    /// あちらは <c>(origin_url, username_element, username_value, password_element, signon_realm)</c> の
    /// 完全一致で弾くので、ここで大小文字を無視すると DB なら別物として入る行を先に捨ててしまう
    /// （要素名は <see cref="LoginDataWriter"/> が常に空で揃える）。</summary>
    internal static List<ImportedPassword> Merge(
        IReadOnlyList<ImportedPassword> existing, IReadOnlyList<ImportedPassword> incoming)
    {
        var merged = new List<ImportedPassword>(existing);
        var index = new Dictionary<(string Origin, string User, string Realm), int>();
        for (var i = 0; i < merged.Count; i++)
            index[Key(merged[i])] = i;
        foreach (var item in incoming)
        {
            if (index.TryGetValue(Key(item), out var at))
                merged[at] = item;   // 後から来たほうが新しい
            else
            {
                index[Key(item)] = merged.Count;
                merged.Add(item);
            }
        }
        return merged;

        static (string, string, string) Key(ImportedPassword p) => (p.Origin, p.Username, p.SignonRealm);
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
