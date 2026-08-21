using Editor.Core.Syntax;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 差分本体（統合／左右並び）に<b>エディタと同じ構文色</b>を付けるためのトークン列を作る。
/// 字句解析はエディタと同一の <see cref="SyntaxEngine"/>（`Editor.Core`）を使うので、同じファイルを
/// エディタで開いたときと差分で読んだときの色が一致する。色そのものは <see cref="EditorSyntaxColors"/>。
///
/// <para><b>旧側と新側は別の文書として解析する。</b> ＋行と−行を1本の流れに混ぜて解析すると、片側にしか
/// 無い引用符やコメント開始でそれ以降の行がまとめて壊れる（削除した <c>/*</c> のせいで残りが全部コメント色、
/// など）。そこで「文脈行＋削除行＝旧ファイル」「文脈行＋追加行＝新ファイル」の2つを組み直して別々に
/// 解析し、表示行へ配り直す。</para>
/// </summary>
internal static class DiffSyntaxHighlighter
{
    /// <summary>これを超える行数の差分は色付けしない。全文コンテキストの左右表示は丸ごと1ファイル分の
    /// 行を持つため、巨大ファイルで組み立て（字句解析＋Run 生成）が重くならないところで頭を打たせる。</summary>
    internal const int MaxLines = 10_000;

    /// <summary>表示行と1対1のトークン列。null の行は色付けしない（ヘッダ・省略マーカー・片側の詰め物）。</summary>
    internal static IReadOnlyList<SyntaxToken[]?> None { get; } = Array.Empty<SyntaxToken[]?>();

    /// <summary>
    /// 統合表示のトークン列を作る。<paramref name="hasPatchPrefix"/> は各行が git パッチの
    /// 1文字プレフィックス（<c>+</c>／<c>-</c>／空白）を含むか（アドホック比較の差分は本文そのもの）。
    /// プレフィックスを持つ差分では、解析はそれを剥がした本文に対して行い、返す列は1桁ずらす。
    /// </summary>
    internal static IReadOnlyList<SyntaxToken[]?> ForUnified(
        string filePath, bool hasPatchPrefix, IReadOnlyList<DiffRowVm> rows)
    {
        if (rows.Count == 0 || rows.Count > MaxLines) return None;
        if (CreateEngine(filePath) is not { } engine) return None;

        var oldLines = new List<string>();
        var newLines = new List<string>();
        var map = new (bool FromOld, int Index)?[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var body = Body(rows[i].Text, hasPatchPrefix);
            switch (rows[i].Kind)
            {
                case "Added":
                    newLines.Add(body);
                    map[i] = (false, newLines.Count - 1);
                    break;
                case "Removed":
                    oldLines.Add(body);
                    map[i] = (true, oldLines.Count - 1);
                    break;
                case "Context":
                    // 文脈行は両方の文書に要る（片方だけだと以降の行の解析状態がずれる）。表示は新側を使う。
                    oldLines.Add(body);
                    newLines.Add(body);
                    map[i] = (false, newLines.Count - 1);
                    break;
                // Header（git ヘッダ）／Gap（@@・省略マーカー）はソースコードではないので色付けしない
            }
        }
        return Distribute(map, Tokenize(engine, oldLines), Tokenize(engine, newLines),
            columnOffset: hasPatchPrefix ? 1 : 0);
    }

    /// <summary>左右並びの片側（<paramref name="left"/>＝旧側）のトークン列を作る。
    /// 左右のテキストはプレフィックスを剥がした本文そのものなので桁のずれは無い。</summary>
    internal static IReadOnlyList<SyntaxToken[]?> ForSide(
        string filePath, IReadOnlyList<DiffSideRowVm> rows, bool left)
    {
        if (rows.Count == 0 || rows.Count > MaxLines) return None;
        if (CreateEngine(filePath) is not { } engine) return None;

        var lines = new List<string>();
        var map = new int[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var (kind, text) = left ? (rows[i].LeftKind, rows[i].LeftText) : (rows[i].RightKind, rows[i].RightText);
            // その側に行がある行だけ（Empty＝詰め物、Gap／Header＝左右共通の注記は対象外）
            if (kind is not ("Context" or "Added" or "Removed")) { map[i] = -1; continue; }
            lines.Add(text);
            map[i] = lines.Count - 1;
        }
        var tokens = Tokenize(engine, lines);
        var result = new SyntaxToken[]?[rows.Count];
        for (var i = 0; i < rows.Count; i++)
            if (map[i] >= 0 && map[i] < tokens.Length)
                result[i] = tokens[map[i]];
        return result;
    }

    /// <summary>拡張子から言語が決まるときだけ字句解析器を返す（決まらなければ色付けしない）。</summary>
    private static SyntaxEngine? CreateEngine(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;
        var engine = new SyntaxEngine();
        engine.DetectLanguage(filePath);
        return engine.LanguageName is null ? null : engine;
    }

    private static string Body(string text, bool hasPatchPrefix)
        => hasPatchPrefix && text.Length > 0 ? text[1..] : text;

    private static SyntaxToken[]?[] Tokenize(SyntaxEngine engine, List<string> lines)
    {
        var byLine = new SyntaxToken[]?[lines.Count];
        if (lines.Count == 0) return byLine;
        try
        {
            foreach (var line in engine.Tokenize(lines.ToArray()))
                if (line.Line >= 0 && line.Line < byLine.Length)
                    byLine[line.Line] = line.Tokens;
        }
        catch
        {
            // 色付けは読みやすさの補助でしかない。どの言語の字句解析が転んでも差分表示自体は出す。
            return new SyntaxToken[]?[lines.Count];
        }
        return byLine;
    }

    /// <summary>解析結果（旧側／新側）を表示行へ配り直す。</summary>
    private static IReadOnlyList<SyntaxToken[]?> Distribute(
        (bool FromOld, int Index)?[] map, SyntaxToken[]?[] oldTokens, SyntaxToken[]?[] newTokens, int columnOffset)
    {
        var result = new SyntaxToken[]?[map.Length];
        for (var i = 0; i < map.Length; i++)
        {
            if (map[i] is not { } at) continue;
            var source = at.FromOld ? oldTokens : newTokens;
            if (at.Index < 0 || at.Index >= source.Length) continue;
            result[i] = Shift(source[at.Index], columnOffset);
        }
        return result;
    }

    private static SyntaxToken[]? Shift(SyntaxToken[]? tokens, int columnOffset)
    {
        if (tokens is null || columnOffset == 0) return tokens;
        var shifted = new SyntaxToken[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
            shifted[i] = tokens[i] with { StartColumn = tokens[i].StartColumn + columnOffset };
        return shifted;
    }
}
