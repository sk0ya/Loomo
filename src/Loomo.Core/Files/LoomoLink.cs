namespace sk0ya.Loomo.Core.Files;

/// <summary><c>loomo://open?…</c> が指す場所：どの部屋（フォルダー）の、どのファイルの、何行目か。</summary>
/// <param name="Folder">部屋のフォルダー（無ければ、受け手がファイルを含む部屋か直近の部屋で開く）。</param>
/// <param name="File">ファイルの絶対パス。</param>
/// <param name="Line">1 始まりの行（0 なら行を指さない）。</param>
/// <param name="Column">1 始まりの列（0 なら行の頭）。</param>
/// <param name="Commit">コミットへのリンク（§24.74）ならそのハッシュ。このとき <paramref name="File"/> はリポジトリのフォルダー。</param>
public sealed record LoomoLinkTarget(string? Folder, string File, int Line, int Column, string? Commit = null);

/// <summary>
/// 部屋の中の場所へのリンク（§24.59）。Obsidian のノート・チャット・Issue のコメントに貼ったリンクから、
/// その部屋のその行へ戻る。<b>開くだけ</b>——コマンドを走らせるリンクは作らない（踏んだだけで何かが実行される口を開けない）。
/// <para>形：<c>loomo://open?folder=&lt;部屋&gt;&amp;file=&lt;ファイル&gt;&amp;line=&lt;行&gt;&amp;col=&lt;列&gt;</c>。
/// パスは絶対パスのまま入れる（この PC の中だけで使うリンクで、部屋は複数フォルダーを持てるので相対にすると基準が決まらない）。</para>
/// </summary>
public static class LoomoLink
{
    public const string Scheme = "loomo";

    public static string Build(string? folder, string file, int line = 0, int column = 0)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(folder))
            parts.Add("folder=" + Uri.EscapeDataString(folder));
        parts.Add("file=" + Uri.EscapeDataString(file));
        if (line > 0)
            parts.Add("line=" + line);
        if (line > 0 && column > 1)
            parts.Add("col=" + column);
        return $"{Scheme}://open?{string.Join("&", parts)}";
    }

    /// <summary>コミットへのリンク（§24.74）：<c>loomo://open?folder=&lt;部屋&gt;&amp;repo=&lt;リポジトリ&gt;&amp;commit=&lt;ハッシュ&gt;</c>。
    /// 踏むとその部屋の Git ペインでそのコミットを選ぶ。開くだけ、はファイルのリンクと同じ。</summary>
    public static string BuildCommit(string? folder, string repository, string hash)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(folder))
            parts.Add("folder=" + Uri.EscapeDataString(folder));
        parts.Add("repo=" + Uri.EscapeDataString(repository));
        parts.Add("commit=" + Uri.EscapeDataString(hash));
        return $"{Scheme}://open?{string.Join("&", parts)}";
    }

    /// <summary>ハッシュとして読める文字列か（16 進の 4〜64 文字）。リンクに任意の rev（<c>HEAD~3</c> やブランチ名）は入れない——
    /// 時間が経つと別のコミットを指すようになる。</summary>
    public static bool IsCommitHash(string? value)
        => value is { Length: >= 4 and <= 64 } && value.All(Uri.IsHexDigit);

    /// <summary>リンクを読む。<c>loomo://open</c> でない・ファイルが無い・パスが絶対でないものは読めない。
    /// ファイルがあるかどうかは見ない（受け手が確かめる）。</summary>
    public static bool TryParse(string? text, out LoomoLinkTarget target)
    {
        target = null!;
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "open", StringComparison.OrdinalIgnoreCase))
            return false;

        string? folder = null, file = null, repo = null, commit = null;
        int line = 0, column = 0;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = pair[..eq];
            string value;
            // 「+」は空白に読み替えない（フォームの書き方ではない。C:\a+b のようなパスを壊す）。
            try { value = Uri.UnescapeDataString(pair[(eq + 1)..]); }
            catch (UriFormatException) { continue; }
            switch (key.ToLowerInvariant())
            {
                case "folder": folder = value; break;
                case "file": file = value; break;
                case "line": _ = int.TryParse(value, out line); break;
                case "col": _ = int.TryParse(value, out column); break;
                case "repo": repo = value; break;
                case "commit": commit = value; break;
            }
        }
        if (folder is not null && !Path.IsPathFullyQualified(folder))
            folder = null;
        if (commit is not null)
        {
            if (!IsCommitHash(commit) || repo is null || !Path.IsPathFullyQualified(repo))
                return false;
            target = new LoomoLinkTarget(folder, repo, 0, 0, commit.ToLowerInvariant());
            return true;
        }
        if (file is null || !Path.IsPathFullyQualified(file))
            return false;
        if (folder is not null && !Path.IsPathFullyQualified(folder))
            folder = null;
        target = new LoomoLinkTarget(folder, file, Math.Max(0, line), Math.Max(0, column));
        return true;
    }
}
