using System;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Files;

/// <summary>テキストから読み取った「ソース上の位置」。
/// <paramref name="Path"/> は<b>未解決</b>（相対のことも Git の <c>a/</c> 接頭辞付きのこともある）で、
/// 実在確認と基準フォルダーの決定は <see cref="SourceLocationResolver"/> の仕事。
/// <paramref name="Line"/> / <paramref name="Column"/> は<b>1 始まり</b>、読み取れなければ 0。</summary>
public readonly record struct SourceLocation(string Path, int Line, int Column);

/// <summary>
/// 選択テキスト（ターミナルの出力・エディタの本文）から「ファイルパス＋行・列」を読み取る純粋関数。
///
/// <para>ここは <b>UI にも実ファイルにも触れない</b>。ターミナル／エディタの右クリックから
/// 「エディタへ送る」（設計書 §23.3 の共通語彙）を出すために、ビルド出力・スタックトレース・
/// grep 出力・Git の diff 見出しといった<b>その場に出ている文字列</b>を宛先として読む。
/// 正規表現をホスト（ShellWindow）側に置くと同じ書式の解釈が散らばるので、入口はこの 1 つに集める。</para>
///
/// <para>解釈する書式:
/// <c>path</c> / <c>path:12</c> / <c>path:12:5</c>、
/// MSBuild・C# コンパイラの <c>path(12,5): error CS0103: …</c>、
/// Python の <c>File "…", line 12</c>、
/// Node/JS スタックの <c>at foo (…:12:5)</c>、
/// 末尾に診断文が続く <c>path:12:5: error: …</c>。
/// Windows のドライブレター（<c>C:\…</c>）は行番号の区切りと読まない。</para>
/// </summary>
public static class SourceLocationParser
{
    /// <summary>Git が diff で付ける接頭辞（<c>a/</c> <c>b/</c> は既定、<c>i/</c> <c>w/</c> <c>c/</c> <c>o/</c> は
    /// <c>diff.mnemonicPrefix</c> のとき index/working tree/commit/object の意味で付く）。</summary>
    private static readonly char[] GitPrefixLetters = { 'a', 'b', 'i', 'w', 'c', 'o' };

    /// <summary>Python のトレースバック行。</summary>
    private static readonly Regex PythonTraceback = new(
        @"^File\s+(?<quote>[""'])(?<path>.+?)\k<quote>(?:\s*,\s*line\s+(?<line>\d+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Node/JS スタックの <c>at 関数名 (パス:行:列)</c>。括弧の中だけを取り出す。</summary>
    private static readonly Regex StackFrameInParens = new(
        @"^at\s+.*\((?<inner>[^()]+)\)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Node/JS スタックの <c>at パス:行:列</c>（関数名なし）。</summary>
    private static readonly Regex StackFrameBare = new(
        @"^at\s+(?<rest>\S.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>MSBuild / C# コンパイラの <c>パス(行,列): error …</c>。
    /// 閉じ括弧の後ろは「無い」か「<c>:</c> で始まる診断文」だけを許して、
    /// <c>foo(1).txt</c> のような普通のファイル名を誤って行番号と読まないようにする。</summary>
    private static readonly Regex MsBuildLocation = new(
        @"^(?<path>.+?)\(\s*(?<line>\d+)\s*(?:,\s*(?<column>\d+)\s*)?\)(?::.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>行・列の後半（<c>12</c> / <c>12:5</c>）。後ろに何が続いていても構わない
    /// （<c>path:12:5: error: …</c> のように診断文が続くため）。</summary>
    private static readonly Regex LeadingLineColumn = new(
        @"^(?<line>\d+)(?::(?<column>\d+))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>前後の空白・引用符・括弧・末尾の句読点を落としたあとの文字列。</summary>
    private static readonly char[] TrailingPunctuation = { ',', ';', ':', '.', '。', '、', '！', '？' };

    /// <summary>選択テキストから位置を読み取る。読み取れなければ false。
    /// 複数行が選ばれていたら<b>最初の中身のある 1 行</b>だけを見る
    /// （診断出力を雑に選んだときに動いてほしいため）。</summary>
    public static bool TryParse(string? text, out SourceLocation location)
    {
        location = default;
        var token = CleanFirstToken(text);
        if (token.Length == 0)
            return false;

        if (PythonTraceback.Match(token) is { Success: true } python)
        {
            var path = python.Groups["path"].Value.Trim();
            if (path.Length == 0)
                return false;
            location = new SourceLocation(path, ParseNumber(python.Groups["line"]), 0);
            return true;
        }

        var body = token;
        if (StackFrameInParens.Match(body) is { Success: true } framed)
            body = Unwrap(framed.Groups["inner"].Value);
        else if (StackFrameBare.Match(body) is { Success: true } bare)
            body = Unwrap(bare.Groups["rest"].Value);
        if (body.Length == 0)
            return false;

        if (MsBuildLocation.Match(body) is { Success: true } msbuild)
        {
            var path = Unwrap(msbuild.Groups["path"].Value);
            if (path.Length == 0)
                return false;
            location = new SourceLocation(
                path, ParseNumber(msbuild.Groups["line"]), ParseNumber(msbuild.Groups["column"]));
            return true;
        }

        location = SplitTrailingLineColumn(body);
        return location.Path.Length > 0;
    }

    /// <summary>選択テキストの「最初の中身のある 1 行」を、引用符・囲み括弧・末尾の句読点を外して返す。
    /// パースに失敗したときの<b>素のパス候補</b>としても使う（<see cref="SourceLocationResolver"/>）。</summary>
    public static string CleanFirstToken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        foreach (var line in text.Split('\n'))
        {
            var unwrapped = Unwrap(line.Replace("\r", ""));
            if (unwrapped.Length > 0)
                return unwrapped;
        }
        return "";
    }

    /// <summary>Git が diff で付ける接頭辞（<c>a/</c> <c>b/</c> <c>i/</c> <c>w/</c> <c>c/</c> <c>o/</c>）を剥がす。
    /// <b>剥がす前のパスが実在するならそちらを優先すべき</b>なので、剥がすかどうかの判断は
    /// 実在確認をする側（<see cref="SourceLocationResolver"/>）が持ち、ここは候補を作るだけ。</summary>
    public static bool TryStripGitPrefix(string? path, out string stripped)
    {
        stripped = "";
        if (path is not { Length: > 2 })
            return false;
        if (path[1] is not ('/' or '\\'))
            return false;
        if (Array.IndexOf(GitPrefixLetters, char.ToLowerInvariant(path[0])) < 0)
            return false;
        stripped = path[2..];
        return stripped.Length > 0;
    }

    /// <summary>末尾の <c>:行[:列]</c> を切り出す。<c>:</c> の後ろが数字でなくなった時点で打ち切り、
    /// 数字が 1 つも無ければパス全体として返す。</summary>
    private static SourceLocation SplitTrailingLineColumn(string token)
    {
        // Windows のドライブレター（C:\… / C:/…）は行番号の区切りではないので走査の開始位置を後ろへずらす。
        var start = HasDriveLetter(token) ? 2 : 0;
        var colon = token.IndexOf(':', start);
        if (colon < 0)
            return new SourceLocation(token, 0, 0);

        var match = LeadingLineColumn.Match(token[(colon + 1)..]);
        if (!match.Success)
            return new SourceLocation(token, 0, 0);

        var path = Unwrap(token[..colon]);
        return path.Length == 0
            ? new SourceLocation(token, 0, 0)
            : new SourceLocation(path, ParseNumber(match.Groups["line"]), ParseNumber(match.Groups["column"]));
    }

    /// <summary><c>C:\…</c> のようなドライブ指定か。1 文字のファイル名（<c>a:12</c>）と区別するため、
    /// <c>:</c> の後ろが区切り文字か終端であることまで見る。</summary>
    private static bool HasDriveLetter(string token)
        => token.Length >= 2
           && char.IsLetter(token[0])
           && token[1] == ':'
           && (token.Length == 2 || token[2] is '/' or '\\');

    private static int ParseNumber(Group group)
        => group.Success && int.TryParse(group.Value, out var value) ? value : 0;

    /// <summary>空白・対になった引用符／括弧・末尾の句読点を落とす（変化しなくなるまで繰り返す）。</summary>
    private static string Unwrap(string text)
    {
        var value = text.Trim();
        while (true)
        {
            var before = value;
            value = value.TrimEnd(TrailingPunctuation).Trim();
            if (value.Length >= 2 && IsPair(value[0], value[^1]))
                value = value[1..^1].Trim();
            if (value == before)
                return value;
        }
    }

    private static bool IsPair(char open, char close)
        => (open == '"' && close == '"')
           || (open == '\'' && close == '\'')
           || (open == '`' && close == '`')
           || (open == '<' && close == '>')
           || (open == '(' && close == ')')
           || (open == '[' && close == ']')
           || (open == '「' && close == '」');
}
