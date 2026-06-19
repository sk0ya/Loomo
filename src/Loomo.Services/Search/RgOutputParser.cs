using System;

namespace sk0ya.Loomo.Services.Search;

/// <summary>ripgrep の <c>--vimgrep</c> 出力 1 行を分解した結果。パスは検索ルート相対。</summary>
public readonly record struct ParsedGrepLine(string Path, int Line, int Column, string Text);

/// <summary>
/// ripgrep の <c>--vimgrep</c> 出力（<c>path:line:col:text</c>）を解析する純ロジック（テスト対象）。
/// 検索を <c>WorkingDirectory=ルート</c>＋検索対象 <c>.</c> で行うため path は相対（ドライブのコロンを含まない）で、
/// 先頭 3 つのコロンだけで分割できる（一致テキストにコロンが含まれても安全）。
/// </summary>
public static class RgOutputParser
{
    public static ParsedGrepLine? ParseVimgrep(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return null;

        var i1 = line.IndexOf(':');
        if (i1 <= 0) return null;
        var i2 = line.IndexOf(':', i1 + 1);
        if (i2 < 0) return null;
        var i3 = line.IndexOf(':', i2 + 1);
        if (i3 < 0) return null;

        if (!int.TryParse(line.AsSpan(i1 + 1, i2 - i1 - 1), out var ln) || ln <= 0)
            return null;
        if (!int.TryParse(line.AsSpan(i2 + 1, i3 - i2 - 1), out var col) || col <= 0)
            return null;

        return new ParsedGrepLine(line[..i1], ln, col, line[(i3 + 1)..]);
    }
}
