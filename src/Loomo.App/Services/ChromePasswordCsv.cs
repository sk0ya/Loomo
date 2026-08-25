namespace sk0ya.Loomo.App.Services;

/// <summary>
/// Chrome / Vivaldi のパスワードマネージャーが書き出す CSV を読む（設計書 §21.5.4）。
///
/// <para><b>これが Chrome からパスワードを移す唯一の道</b>。Chrome 127 以降はアプリ束縛暗号
/// （<c>v20</c>）で保存されていて、鍵は Chrome 自身の COM サービスが呼び出し元の実行ファイルを
/// 検証してからしか渡さない——つまり <c>Login Data</c> を直接読む手は<b>原理的に</b>使えない
/// （<see cref="ChromiumCrypto"/> 参照）。そこで、ブラウザ自身に正規の手続きで書き出させたものを受ける。
/// 利用者の手順は <c>chrome://password-manager/settings</c> →「パスワードをエクスポート」
/// （Windows のログイン認証が入る）。</para>
///
/// <para>列は <c>name,url,username,password,note</c> が既定だが、版によって
/// <c>note</c> が無かったり順番が違ったりする。<b>見出し行を読んで位置を決める</b>——
/// 決め打ちにすると、ずれた版でパスワード欄に URL が入るという最悪の壊れ方をする。</para>
/// </summary>
public static class ChromePasswordCsv
{
    /// <summary>読み込む。1行の欠けは飛ばして残りを通す（数千件のうち1行のために全部を諦めない）。</summary>
    public static ImportRead<ImportedPassword> Read(string filePath)
    {
        string text;
        try
        {
            text = File.ReadAllText(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ImportRead<ImportedPassword>.Empty($"CSV を読めませんでした: {ex.Message}");
        }

        var rows = ParseCsv(text);
        if (rows.Count == 0)
            return ImportRead<ImportedPassword>.Empty("CSV が空です。");

        var header = rows[0];
        var url = IndexOf(header, "url", "origin");
        var user = IndexOf(header, "username", "login");
        var password = IndexOf(header, "password");
        if (url < 0 || user < 0 || password < 0)
            return ImportRead<ImportedPassword>.Empty(
                "CSV の見出し行に url / username / password が見つかりません。"
                + "ブラウザのパスワードマネージャーから書き出したファイルを選んでください。");

        var items = new List<ImportedPassword>();
        var blocked = 0;
        foreach (var row in rows.Skip(1))
        {
            if (row.Count <= Math.Max(url, Math.Max(user, password)))
            {
                blocked++;
                continue;
            }
            var origin = row[url].Trim();
            var secret = row[password];
            if (origin.Length == 0 || secret.Length == 0)
            {
                blocked++;
                continue;
            }
            items.Add(new ImportedPassword(
                origin, SignonRealmOf(origin), row[user], secret, DateTime.UtcNow));
        }
        return new ImportRead<ImportedPassword>(items, blocked, null);
    }

    /// <summary>Chromium が鍵にする「保護領域」。フォームのログインでは
    /// <c>https://example.com/</c> のようにオリジンまでで、パスまでは含めない——
    /// ここを URL のまま入れると、保存はされるのに<b>どのページでも自動入力されない</b>行ができる。</summary>
    private static string SignonRealmOf(string origin)
        => Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Authority}/"
            : origin;

    private static int IndexOf(IReadOnlyList<string> header, params string[] names)
    {
        for (var i = 0; i < header.Count; i++)
        {
            var cell = header[i].Trim().Trim('"');
            if (names.Any(n => cell.Equals(n, StringComparison.OrdinalIgnoreCase)))
                return i;
        }
        return -1;
    }

    /// <summary>RFC 4180 の最小実装。<b>引用符の中の改行とカンマ</b>を通せることがここでの要点で、
    /// メモ欄に改行を書いた項目が1つあるだけで、素朴な <c>Split</c> は以降の行を全部ずらす。</summary>
    internal static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new System.Text.StringBuilder();
        var quoted = false;
        var hasContent = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"')
                {
                    cell.Append(c);
                    continue;
                }
                // "" は引用符そのもの、単独の " は引用の終わり。
                if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                    continue;
                }
                quoted = false;
                continue;
            }
            switch (c)
            {
                case '"':
                    quoted = true;
                    hasContent = true;
                    break;
                case ',':
                    row.Add(cell.ToString());
                    cell.Clear();
                    hasContent = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(cell.ToString());
                    cell.Clear();
                    if (hasContent)
                        rows.Add(row);
                    row = new List<string>();
                    hasContent = false;
                    break;
                default:
                    cell.Append(c);
                    hasContent = true;
                    break;
            }
        }
        if (hasContent || cell.Length > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }
        // 先頭の BOM は見出し名の一致を静かに壊す（"url" が "﻿url" になる）。
        if (rows.Count > 0 && rows[0].Count > 0)
            rows[0][0] = rows[0][0].TrimStart('﻿');
        return rows;
    }
}
