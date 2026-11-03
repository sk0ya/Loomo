namespace sk0ya.Loomo.App.Services;

/// <summary>
/// ブラウザのタブが「どこから調べ始めたか」（§41.12）。エディタ・ターミナルの右クリック「ブラウザへ送る」が
/// 作ったタブに付き、そのタブの中でリンクを辿っても、<c>target=_blank</c> で開いた子のタブにも引き継ぐ。
/// ブラウザサポートはこれを見て「戻る」と、ページのコードを「そこへ貼る」を出す——調べ物を元の作業へ戻す道。
/// <para>覚えるのはこのセッションの間だけ（タブを閉じれば消える。復元はしない）。</para>
/// </summary>
public abstract record BrowserSearchOrigin(string Query)
{
    /// <summary>エディタのファイルの選んだ所（行・列は 0 始まり、<paramref name="EndLine"/> は選択の最後の行）。</summary>
    public sealed record Editor(string Query, string FilePath, int Line, int Column, int EndLine) : BrowserSearchOrigin(Query);

    /// <summary>ターミナルのタブ（出力の一部を選んで調べた）。</summary>
    public sealed record Terminal(string Query, Guid TabId) : BrowserSearchOrigin(Query);

    /// <summary>行の見出しに出す調べた語（長ければ詰める）。</summary>
    public string QueryPreview => Shorten(Query.ReplaceLineEndings(" ").Trim(), 60);

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>
    /// ページのコードを、元のファイルの選んだ所の<b>下の行</b>へ差し込む形にする（純粋）。選んだ所は調べた語（エラーの文・
    /// API 名）そのものなので置き換えない。差し込む行は、選んだ所の最後の行の字下げに揃え（コードブロックの共通の字下げは
    /// 外してから）、改行はファイルのもの（CRLF／LF）に合わせる。返すのは差し込む位置（0 始まり）と中身、差し込んだ最初と最後の行。
    /// </summary>
    public static (int Line, int Column, string Text, int FirstInsertedLine, int LastInsertedLine) PasteBelow(string document, int endLine, string block)
    {
        var newline = document.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = document.Split('\n');
        var line = Math.Clamp(endLine, 0, lines.Length - 1);
        var anchor = lines[line].TrimEnd('\r');
        var indent = anchor[..(anchor.Length - anchor.TrimStart(' ', '\t').Length)];
        var blockLines = block.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var common = blockLines.Where(l => l.Trim().Length > 0)
            .Select(l => l.Length - l.TrimStart(' ', '\t').Length)
            .DefaultIfEmpty(0).Min();
        var body = string.Join(newline, blockLines.Select(l => l.Trim().Length == 0 ? "" : indent + l[common..]));
        return (line, anchor.Length, newline + body, line + 1, line + blockLines.Length);
    }
}
