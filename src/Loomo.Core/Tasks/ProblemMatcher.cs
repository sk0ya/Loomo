using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Tasks;

/// <summary>matcher が拾った相対パスの解決方法（tasks.json の <c>fileLocation</c>）。</summary>
public enum ProblemFileLocation
{
    /// <summary>出力のパスは絶対パス。</summary>
    Absolute,
    /// <summary>基準フォルダー相対（基準は <see cref="ProblemMatcher.FileLocationBase"/>）。</summary>
    Relative,
    /// <summary>基準相対で実在すればそれ、無ければ絶対として読む。</summary>
    AutoDetect,
}

/// <summary>
/// problem matcher の1行分の正規表現と、各グループが何を表すか（VS Code の problemPattern と同じ語彙）。
/// グループ番号 0 は「使わない」。ただし <see cref="Message"/> の 0 は VS Code と同じく「一致全体」。
/// </summary>
public sealed record ProblemPattern(Regex Regex)
{
    public int File { get; init; }
    /// <summary><c>(line)</c> / <c>(line,col)</c> / <c>(line,col,endLine,endCol)</c> をまとめて持つグループ（msCompile）。</summary>
    public int Location { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    public int EndLine { get; init; }
    public int EndColumn { get; init; }
    public int Severity { get; init; }
    public int Code { get; init; }
    public int? Message { get; init; }
    /// <summary>複数行パターンの最後の行が、続く行にも繰り返し当たるか（eslint stylish の各指摘行）。</summary>
    public bool Loop { get; init; }
}

/// <summary>
/// 終わらないタスク（watch）の1サイクルの区切り（tasks.json の <c>problemMatcher.background</c>）。
/// <see cref="BeginsPattern"/> に当たる行で新しいサイクルが始まり、<see cref="EndsPattern"/> に当たる行で
/// そのサイクルの問題が出そろう（設計書 §31.17.5）。
/// </summary>
/// <param name="ActiveOnStart">起動直後が既にサイクルの途中か（begins を待たずに集め始める）。VS Code の既定は false
/// だが、tsc の「Starting compilation」のように最初のサイクルも begins で始まるものが大半なので、どちらでも動く。</param>
public sealed record ProblemMatcherBackground(Regex? BeginsPattern, Regex? EndsPattern, bool ActiveOnStart = false);

/// <summary>
/// problem matcher 1つ（設計書 §31.17）。<see cref="Patterns"/> が2つ以上なら複数行パターンで、
/// 連続する行に順に当たったときだけ1件になる（最後の行が <see cref="ProblemPattern.Loop"/> なら繰り返す）。
/// </summary>
public sealed record ProblemMatcher(string Owner, IReadOnlyList<ProblemPattern> Patterns)
{
    /// <summary>既定は VS Code と同じく「ワークスペースフォルダー相対」。</summary>
    public ProblemFileLocation FileLocation { get; init; } = ProblemFileLocation.Relative;
    /// <summary>相対パスの基準（未展開。<c>${workspaceFolder}</c> / <c>${cwd}</c>＝タスクの作業フォルダー）。</summary>
    public string FileLocationBase { get; init; } = "${workspaceFolder}";
    /// <summary>重要度を取れない（グループ無し・読めない語）ときの既定。</summary>
    public TaskProblemSeverity? DefaultSeverity { get; init; }
    /// <summary>watch のサイクルの区切り。null なら区切りの無い matcher（watch に付けた場合はタスク終了時に1回だけ当てる）。</summary>
    public ProblemMatcherBackground? Background { get; init; }
}

/// <summary>
/// 名前で参照できる組み込み matcher（tasks.json の <c>"$msCompile"</c> 等）。正規表現は VS Code の定義を
/// 下敷きにしたうえで、Loomo の可視ターミナルから取れる出力に合わせて2点だけ緩めてある：
/// 行頭の空白を許す（.NET の Terminal Logger は診断を字下げして出す）、MSBuild の末尾
/// <c>[proj.csproj]</c> はメッセージから落とす（同じ診断の重複排除が効かなくなるのと、読みにくいため）。
/// </summary>
public static class BuiltInProblemMatchers
{
    private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    /// <summary>MSBuild / C# コンパイラ：<c>path(12,5): error CS1002: msg [proj.csproj]</c>。
    /// 行頭の <c>12&gt;</c>（並列ビルドのノード番号）と <c>fatal error</c> も読む。</summary>
    public static ProblemMatcher MsCompile { get; } = new("msCompile",
    [
        new ProblemPattern(new Regex(
            @"^\s*(?:\d+>)?\s*(\S.*?)\((\d+|\d+,\d+|\d+,\d+,\d+,\d+)\)\s*:\s+((?:fatal +)?error|warning|info)\s+(\w+\d+)\s*:\s*(.*?)(?:\s+\[[^\[\]]+\])?\s*$",
            Options | RegexOptions.IgnoreCase))
        { File = 1, Location = 2, Severity = 3, Code = 4, Message = 5 },
    ])
    { FileLocation = ProblemFileLocation.Absolute };

    /// <summary>TypeScript（<c>--pretty false</c>）：<c>src/a.ts(7,5): error TS2322: msg</c> と
    /// <c>src/a.ts:7:5 - error TS2322: msg</c>。パスはタスクの作業フォルダー相対。</summary>
    public static ProblemMatcher Tsc { get; } = new("typescript",
    [
        new ProblemPattern(new Regex(
            @"^\s*([^\s].*?)[\(:](\d+)[,:](\d+)(?:\):\s+|\s+-\s+)(error|warning|info)\s+(TS\d+)\s*:\s*(.*)$",
            Options))
        { File = 1, Line = 2, Column = 3, Severity = 4, Code = 5, Message = 6 },
    ])
    { FileLocation = ProblemFileLocation.Relative, FileLocationBase = "${cwd}" };

    /// <summary>ESLint の既定（stylish）書式。ファイル名の行に、字下げした指摘行が続く複数行パターン：
    /// <code>
    /// C:\app\src\a.js
    ///   3:7  error    'x' is assigned a value but never used  no-unused-vars
    /// </code></summary>
    public static ProblemMatcher EslintStylish { get; } = new("eslint",
    [
        new ProblemPattern(new Regex(@"^([^\s].*)$", Options)) { File = 1 },
        new ProblemPattern(new Regex(
            @"^\s+(\d+):(\d+)\s+(error|warning|info)\s+(.+?)(?:\s\s+(\S+))?$", Options))
        { Line = 1, Column = 2, Severity = 3, Message = 4, Code = 5, Loop = true },
    ])
    { FileLocation = ProblemFileLocation.AutoDetect };

    /// <summary>ESLint の compact 書式：<c>path: line 3, col 7, Error - msg (rule)</c>。</summary>
    public static ProblemMatcher EslintCompact { get; } = new("eslint",
    [
        new ProblemPattern(new Regex(
            @"^(.+):\sline\s(\d+),\scol\s(\d+),\s(Error|Warning|Info)\s-\s(.+)\s\((.+)\)$", Options))
        { File = 1, Line = 2, Column = 3, Severity = 4, Message = 5, Code = 6 },
    ])
    { FileLocation = ProblemFileLocation.AutoDetect };

    /// <summary>gcc / clang：<c>src/a.c:12:5: error: msg</c>（列は省略されることがある）。</summary>
    public static ProblemMatcher Gcc { get; } = new("cpp",
    [
        new ProblemPattern(new Regex(
            @"^\s*(.*?):(\d+):(\d*):?\s+(?:fatal\s+)?(warning|error|note):\s+(.*)$", Options))
        { File = 1, Line = 2, Column = 3, Severity = 4, Message = 5 },
    ])
    { FileLocation = ProblemFileLocation.AutoDetect };

    /// <summary>tsc の watch（<c>tsc --watch</c>）。行の書式は <see cref="Tsc"/> と同じで、1回のコンパイルを
    /// 「Starting compilation…／File change detected…」から「Found N errors. Watching for file changes.」までで区切る。
    /// 行頭の時刻（<c>[10:21:03 AM]</c> や <c>10:21:03 - </c>）は書式が地域で変わるので見ない。</summary>
    public static ProblemMatcher TscWatch { get; } = Tsc with
    {
        Background = new ProblemMatcherBackground(
            new Regex(@"(?:Starting compilation in watch mode|File change detected\. Starting incremental compilation)\.\.\.", Options),
            new Regex(@"(?:Compilation complete\.|Found \d+ errors?\b.*?) Watching for file changes\.", Options)),
    };

    private static readonly Dictionary<string, ProblemMatcher> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["$msCompile"] = MsCompile,
        ["$tsc"] = Tsc,
        // watch 版は終わらないタスク用。行の書式は同じで、コンパイル1回ごとの区切りを持つ。
        ["$tsc-watch"] = TscWatch,
        ["$eslint-stylish"] = EslintStylish,
        ["$eslint-compact"] = EslintCompact,
        ["$gcc"] = Gcc,
    };

    /// <summary>組み込み名の一覧（未知の名前の警告に添える）。</summary>
    public static IReadOnlyCollection<string> Names => ByName.Keys;

    public static ProblemMatcher? Find(string name)
        => ByName.TryGetValue(name.Trim(), out var matcher) ? matcher : null;
}
