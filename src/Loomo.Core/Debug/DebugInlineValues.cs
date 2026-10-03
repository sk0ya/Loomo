using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Debug;

/// <summary>行末に出す 1 行分の値（<c>x = 3, name = "abc"</c>）。<see cref="Line0"/> はエディタの 0 始まり。</summary>
public sealed record DebugInlineValueLine(int Line0, string Text);

/// <summary>
/// あるソースに出している行末の値の一揃い（デバッグの Inline Values）。停止中のフレーム 1 つぶん。
/// <see cref="SourcePath"/> が null／<see cref="Lines"/> が空なら「何も出さない」（続行・終了で古い値を消すとき）。
/// </summary>
public sealed record DebugInlineValueSet(string? SourcePath, IReadOnlyList<DebugInlineValueLine> Lines)
{
    public static DebugInlineValueSet Empty { get; } = new(null, Array.Empty<DebugInlineValueLine>());

    public bool IsEmpty => SourcePath is null || Lines.Count == 0;

    /// <summary>そのファイルを開いているエディタに出す行（別のファイル・空なら空）。パスは大文字小文字と
    /// 相対表記の違いを吸収して比べる（DAP の source.path とエディタのパスは綴りが揃っているとは限らない）。</summary>
    public IReadOnlyList<DebugInlineValueLine> LinesFor(string? editorPath)
    {
        if (IsEmpty || string.IsNullOrWhiteSpace(editorPath)) return Array.Empty<DebugInlineValueLine>();
        try
        {
            return string.Equals(System.IO.Path.GetFullPath(editorPath), System.IO.Path.GetFullPath(SourcePath!),
                StringComparison.OrdinalIgnoreCase)
                ? Lines
                : Array.Empty<DebugInlineValueLine>();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            return Array.Empty<DebugInlineValueLine>();
        }
    }
}

/// <summary>
/// デバッグ停止中の「行末の値」（VS Code の Debug Inline Values）を組み立てる純ロジック。
///
/// <para>VS Code の既定実装と同じ素朴な方式：停止フレームのローカル変数（DAP の scopes→variables）の名前と、
/// <b>関数の先頭〜停止行</b>のソースに出てくる識別子を突き合わせ、出てきた行の末尾に <c>名前 = 値</c> を並べる。
/// 言語サーバーの <c>textDocument/inlineValue</c> も式評価も使わない——アダプタ（netcoredbg／同梱 NetFx／
/// js-debug）を問わず、すでに取れている変数一覧だけで完結させるため。</para>
///
/// <para>範囲を停止行より下へ広げないのは、そこはまだ実行されておらず、値が「これから」の行に
/// 並ぶと読み違えるから。関数の先頭は波括弧の対応から推定する（<see cref="FindFunctionStart"/>）。</para>
/// </summary>
public static class DebugInlineValues
{
    /// <summary>関数の先頭を探して遡る上限（行）。見つからなければここで打ち切る。</summary>
    public const int MaxLookbackLines = 300;

    /// <summary>1 つの値の表示上限（文字）。長い文字列やコレクションの要約で行が埋まらないように。</summary>
    public const int MaxValueChars = 60;

    /// <summary>1 行の表示上限（文字）。VS Code の既定（150）に合わせる。</summary>
    public const int MaxLineChars = 150;

    // 識別子（C# の @verbatim と JS の $ を許す）。直前が '.' のもの（メンバアクセス）は呼び出し側で捨てる。
    private static readonly Regex Identifier = new(@"@?[A-Za-z_$][A-Za-z0-9_$]*", RegexOptions.Compiled);

    // 変数名として突き合わせる価値のある名前（DAP は "[0]" や "Static members" も返すので弾く）。
    private static readonly Regex VariableName = new(@"^[A-Za-z_$][A-Za-z0-9_$]*$", RegexOptions.Compiled);

    // 波括弧を開くが「関数」ではない構文（中に入っても同じフレームのまま）。
    private static readonly HashSet<string> BlockKeywords = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "foreach", "while", "do", "switch", "try", "catch", "finally",
        "using", "lock", "fixed", "unsafe", "checked", "unchecked", "case", "default",
        "with", "await",
    };

    // 型・名前空間の宣言（停止行がこの直下＝フィールド初期化子などなら、範囲は停止行だけにする）。
    private static readonly Regex TypeDeclaration = new(
        @"\b(class|struct|interface|record|enum|namespace|module)\b", RegexOptions.Compiled);

    /// <summary>
    /// 停止行 <paramref name="stopLine0"/>（0 始まり）を含む関数の先頭行を推定する。
    /// 停止行の上を逆向きに走査して対応の取れない <c>{</c> を探し、その見出しが
    /// if/for/try などの制御構文ならさらに外側へ、メソッド・ローカル関数・ラムダ・<c>function</c> なら
    /// そこを先頭とする。型宣言にぶつかったら停止行そのもの、何も見つからなければ遡れた所まで。
    /// コメントと文字列リテラルの中の括弧は数えない。
    /// </summary>
    public static int FindFunctionStart(IReadOnlyList<string> lines, int stopLine0)
    {
        if (lines.Count == 0) return 0;
        stopLine0 = Math.Clamp(stopLine0, 0, lines.Count - 1);
        int floor = Math.Max(0, stopLine0 - MaxLookbackLines);
        int depth = 0;

        // ステップインの直後は関数の開き '{' の行で止まる（netcoredbg）。その '{' は自分の関数のもの。
        var stopCode = AutosExtractor.StripCommentsAndStrings(lines[stopLine0]).TrimStart();
        if (stopCode.StartsWith('{'))
        {
            var (header, headerLine) = HeaderOf(lines, stopLine0, "");
            if (Classify(header) == BlockKind.Function) return headerLine;
        }

        for (int l = stopLine0 - 1; l >= floor; l--)
        {
            var code = AutosExtractor.StripCommentsAndStrings(lines[l]);
            for (int c = code.Length - 1; c >= 0; c--)
            {
                if (code[c] == '}') { depth++; continue; }
                if (code[c] != '{') continue;
                if (depth > 0) { depth--; continue; }

                // 対応の取れない '{'＝停止行を囲むブロックの始まり。何のブロックかを見出しで決める。
                var (header, headerLine) = HeaderOf(lines, l, code[..c]);
                switch (Classify(header))
                {
                    case BlockKind.Function: return headerLine;
                    case BlockKind.Type: return stopLine0;
                    // 制御構文・初期化子：同じフレームの内側なので、さらに外側の '{' を探す。
                }
            }
        }
        return floor;
    }

    private enum BlockKind { Function, Block, Type }

    /// <summary>'{' の見出し（その行の '{' より前、空なら直前の空でない行）と、その行番号。</summary>
    private static (string Header, int Line) HeaderOf(IReadOnlyList<string> lines, int line, string beforeBrace)
    {
        var header = beforeBrace.Trim();
        if (header.Length > 0 && header != "}") return (header, line);
        for (int l = line - 1; l >= 0 && l >= line - 3; l--)
        {
            var prev = AutosExtractor.StripCommentsAndStrings(lines[l]).Trim();
            if (prev.Length > 0) return (prev, l);
        }
        return ("", line);
    }

    private static BlockKind Classify(string header)
    {
        // "} else" "} catch (Exception e)" のように閉じ括弧が先頭に付いた見出しを素にする。
        var h = header.TrimStart('}', ' ', '\t');
        if (h.Length == 0) return BlockKind.Block;

        if (h.EndsWith("=>", StringComparison.Ordinal)) return BlockKind.Function;          // ラムダ・アロー関数
        if (Regex.IsMatch(h, @"\bfunction\b")) return BlockKind.Function;                    // JS の function
        if (TypeDeclaration.IsMatch(h) && !h.Contains('(')) return BlockKind.Type;

        var first = Identifier.Match(h);
        if (first.Success && BlockKeywords.Contains(first.Value)) return BlockKind.Block;
        // オブジェクト／コレクション初期化子・配列・引数の途中（"= new Foo" "= {" "(" ","）は関数ではない。
        if (h.EndsWith('=') || h.EndsWith('(') || h.EndsWith(',') || h.EndsWith('[')
            || Regex.IsMatch(h, @"(=|\breturn)\s*(new\b[^()]*)?(\(\s*\))?$"))
            return BlockKind.Block;
        // プロパティのアクセサ（get / set / init）は関数とみなす（そのフレームの中身はそのアクセサ）。
        // 残り＝メソッド・コンストラクタ・ローカル関数・TS のメソッド短縮記法など「(...)」を伴う見出し。
        if (h.Contains(')') || h is "get" or "set" or "init") return BlockKind.Function;
        return BlockKind.Block;
    }

    /// <summary>
    /// <paramref name="startLine0"/>〜<paramref name="stopLine0"/>（両端含む）の各行に出てくる変数を拾い、
    /// 行ごとの表示文面を作る。<paramref name="values"/> は「変数名 → 表示値」（先に入れたスコープ優先は
    /// 呼び出し側の責務）。同じ行に同じ名前が何度出ても 1 回、並びは行内の出現順。
    /// メンバアクセス（<c>obj.x</c> の <c>x</c>）は別物なので拾わない。
    /// </summary>
    public static IReadOnlyList<DebugInlineValueLine> Build(
        IReadOnlyList<string> lines, int startLine0, int stopLine0, IReadOnlyDictionary<string, string> values)
    {
        var result = new List<DebugInlineValueLine>();
        if (lines.Count == 0 || values.Count == 0) return result;
        startLine0 = Math.Clamp(startLine0, 0, lines.Count - 1);
        stopLine0 = Math.Clamp(stopLine0, startLine0, lines.Count - 1);

        for (int l = startLine0; l <= stopLine0; l++)
        {
            var code = AutosExtractor.StripCommentsAndStrings(lines[l]);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var text = new StringBuilder();
            foreach (Match m in Identifier.Matches(code))
            {
                if (IsMemberAccess(code, m.Index)) continue;
                var name = m.Value.TrimStart('@');
                if (!seen.Add(name) || !values.TryGetValue(name, out var value)) continue;

                var piece = $"{name} = {FormatValue(value)}";
                if (text.Length > 0)
                {
                    if (text.Length + 2 + piece.Length > MaxLineChars) { text.Append(", …"); break; }
                    text.Append(", ");
                }
                text.Append(piece);
            }
            if (text.Length > 0) result.Add(new DebugInlineValueLine(l, text.ToString()));
        }
        return result;
    }

    /// <summary>識別子の直前（空白を飛ばして）が '.' か '?.' ならメンバアクセス。</summary>
    private static bool IsMemberAccess(string code, int index)
    {
        int i = index - 1;
        while (i >= 0 && char.IsWhiteSpace(code[i])) i--;
        return i >= 0 && code[i] == '.';
    }

    /// <summary>
    /// DAP の変数一覧から「名前 → 値」を作る。先に来た名前を優先する（Locals を Closure・外側スコープより先に
    /// 渡せば、内側の変数が外側の同名を隠す——言語の見え方と同じ）。識別子でない名前（<c>[0]</c>、
    /// "Static members" 等のグループ）と <c>this</c> は捨てる。
    /// </summary>
    public static Dictionary<string, string> ToValueMap(IEnumerable<DebugVariable> variables)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in variables)
        {
            var name = v.Name.TrimStart('@');
            if (name is "this" or "base" || !VariableName.IsMatch(name)) continue;
            map.TryAdd(name, v.Value);
        }
        return map;
    }

    /// <summary>行末に並べるためのスコープ選び。重い（expensive）スコープと JS のグローバル・スクリプト
    /// スコープは読まない（巨大で、関数内の行に出る名前ではほぼ当たらない）。</summary>
    public static bool ShouldReadScope(DebugScope scope)
        => !scope.Expensive
           && !scope.Name.StartsWith("Global", StringComparison.OrdinalIgnoreCase)
           && !scope.Name.StartsWith("Script", StringComparison.OrdinalIgnoreCase)
           && !scope.Name.StartsWith("Static", StringComparison.OrdinalIgnoreCase)
           && !scope.Name.StartsWith("Registers", StringComparison.OrdinalIgnoreCase);

    /// <summary>値を 1 行に収める：改行・連続空白を 1 つの空白へ、上限を超えたら末尾を「…」で切る。</summary>
    public static string FormatValue(string value)
    {
        var flat = Regex.Replace(value ?? "", @"\s+", " ").Trim();
        return flat.Length <= MaxValueChars ? flat : flat[..(MaxValueChars - 1)] + "…";
    }

    /// <summary>ソース全体・停止行・変数一覧から、そのまま描ける一揃いを作る（範囲の推定＋突き合わせ）。</summary>
    public static DebugInlineValueSet Compute(
        string sourcePath, IReadOnlyList<string> lines, int stopLine0, IReadOnlyDictionary<string, string> values)
    {
        if (lines.Count == 0 || stopLine0 < 0 || stopLine0 >= lines.Count) return DebugInlineValueSet.Empty;
        int start = FindFunctionStart(lines, stopLine0);
        return new DebugInlineValueSet(sourcePath, Build(lines, start, stopLine0, values));
    }
}
