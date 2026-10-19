namespace sk0ya.Loomo.App.Services;

/// <summary>ターミナルサポートの面が何を見せるか（§39.2）。並びは優先順ではなく種類。</summary>
internal enum TerminalSupportMode
{
    /// <summary>ターミナルのタブが1つも無い。</summary>
    NoTab,
    /// <summary>シェルがプロンプトを出して待っている（前面に何も居ない）。</summary>
    Shell,
    /// <summary>シェル以外のプログラムが前面に居る（OSC 133 の C を見て D 未着）。TUI もそうでないものも。</summary>
    Program,
    /// <summary>Claude Code が前面に居る（タイトルの印、§24.1.1）。</summary>
    ClaudeCode,
}

/// <summary>
/// ターミナルサポートが追従している「いま前に出ているターミナルのタブ」の様子（§39.2）。
/// EditorSupport の追従元＝アクティブなエディタのタブにあたる。ShellWindow がタブの切り替え・タイトル・
/// OSC 133 から組み立てて渡す。値は組み立てた時点のもの。
/// </summary>
internal sealed record TerminalSupportSubject(
    Guid TabId,
    string TabName,
    string? WorkingDirectory,
    string? RunningCommand,
    DateTime? RunningSinceUtc,
    ClaudeAgentTab? Agent)
{
    /// <summary>見せ方を決める。<b>Claude を先に見る</b>——Claude Code はプロンプトの C を出したまま居座るので
    /// 「動いているプログラム」でもあるが、そこで見たいのはコマンド行ではなく Claude のセッションの中身。
    /// 常駐セッションの復元直後のように C が来ないまま Claude だけ分かる場合（§38.7）もこれで拾える。</summary>
    public TerminalSupportMode Mode
        => Agent is not null || TerminalProgramCatalog.Match(RunningCommand)?.Id == TerminalProgramCatalog.ClaudeId
            ? TerminalSupportMode.ClaudeCode
            : RunningCommand is not null ? TerminalSupportMode.Program
            : TerminalSupportMode.Shell;
}

/// <summary>TUI の操作キー1つ（「q」→「終了」）。</summary>
internal sealed record TerminalKeyHint(string Keys, string Action);

/// <summary>前面のプログラムの見立て（§39.3）：名前・一行の説明・よく使う操作キー。</summary>
internal sealed record TerminalProgram(string Id, string Name, string Description, IReadOnlyList<TerminalKeyHint> Keys);

/// <summary>
/// 前面に居るコマンド行から、知っているプログラムを引く（§39.3）。拡張子で提供者を選ぶ EditorSupport の
/// 表にあたる。<b>載せるキーは確かめたものだけ</b>——TUI のキーは版で変わり、間違った案内は案内が無いより悪い
/// （押して別のことが起きる）。知らないプログラムは null で、面はコマンド行と経過時間だけを出す。
/// </summary>
internal static class TerminalProgramCatalog
{
    public const string ClaudeId = "claude";

    private static readonly TerminalProgram[] Programs =
    [
        new(ClaudeId, "Claude Code", "ターミナルのコーディングエージェント", [
            new("Esc", "いまの応答を止める"),
            new("Esc Esc", "前の入力へ巻き戻す"),
            new("Shift+Tab", "権限モードを切り替える"),
            new("Ctrl+G", "プロンプトを Loomo のエディタで書く（§38.5）"),
            new("Ctrl+O", "詳しい記録の表示を切り替える"),
            new("!", "行頭でシェルのコマンドを直接実行"),
            new("@", "ファイルを参照に足す"),
            new("/compact", "会話を要約してコンテキストを空ける"),
            new("/clear", "会話を消して始め直す"),
        ]),
        new("vim", "Vim", "テキストエディタ", [
            new("i", "挿入モードへ"),
            new("Esc", "ノーマルモードへ戻る"),
            new(":w", "保存"),
            new(":q", "終了"),
            new(":q!", "保存せずに終了"),
            new(":wq", "保存して終了"),
            new("/", "検索（n で次）"),
            new("u", "取り消し"),
        ]),
        new("less", "less", "ページャ（git log・git diff などの出力もこれ）", [
            new("q", "終了"),
            new("Space / b", "次 / 前のページ"),
            new("g / G", "先頭 / 末尾へ"),
            new("/", "検索（n / N で次 / 前）"),
        ]),
        new("lazygit", "lazygit", "Git の TUI", [
            new("?", "このパネルのキー一覧"),
            new("1〜5", "パネルを切り替える"),
            new("Space", "ステージする / 外す"),
            new("a", "すべてステージ"),
            new("c", "コミット"),
            new("p / P", "pull / push"),
            new("q", "終了"),
        ]),
        new("htop", "htop", "プロセスの一覧", [
            new("F3 / /", "検索"),
            new("F5 / t", "ツリー表示"),
            new("F6", "並べ替えの列を選ぶ"),
            new("F9 / k", "プロセスへシグナルを送る"),
            new("F10 / q", "終了"),
        ]),
        new("ssh", "ssh", "リモートのシェル", [
            new("Enter ~ .", "固まった接続を切る"),
            new("exit", "リモートのシェルを抜ける"),
        ]),
        new("python", "Python REPL", "対話モードの Python", [
            new("exit()", "終了"),
            new("help()", "ヘルプ"),
        ]),
        new("node", "Node.js REPL", "対話モードの Node.js", [
            new(".exit", "終了"),
            new(".help", "REPL のコマンド一覧"),
        ]),
        new("dotnet-watch", "dotnet watch", "変更を見張って作り直す", [
            new("Ctrl+R", "作り直して再起動"),
            new("Ctrl+C", "止める"),
        ]),
    ];

    /// <summary>実行ファイル名（拡張子なし・小文字）→ プログラム。</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = ClaudeId,
        ["vim"] = "vim", ["nvim"] = "vim", ["vi"] = "vim", ["gvim"] = "vim",
        ["less"] = "less",
        ["lazygit"] = "lazygit", ["lg"] = "lazygit",
        ["htop"] = "htop",
        ["ssh"] = "ssh",
        ["python"] = "python", ["python3"] = "python", ["py"] = "python",
        ["node"] = "node",
    };

    /// <summary>コマンド行から知っているプログラムを引く。REPL は引数なしで起動したときだけ
    /// （<c>python build.py</c> はスクリプトの実行で、REPL の案内は的外れ）。</summary>
    public static TerminalProgram? Match(string? commandLine)
    {
        var tokens = Tokenize(commandLine);
        if (tokens.Count == 0)
            return null;
        var exe = ExecutableName(tokens[0]);
        if (exe.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && tokens.Count > 1 && tokens[1].Equals("watch", StringComparison.OrdinalIgnoreCase))
            return Find("dotnet-watch");
        if (!Aliases.TryGetValue(exe, out var id))
            return null;
        if (id is "python" or "node" && tokens.Count > 1)
            return null;
        return Find(id);
    }

    private static TerminalProgram? Find(string id) => Programs.FirstOrDefault(p => p.Id == id);

    /// <summary>pwsh の呼び出し演算子（<c>&amp; 'C:\…\claude.exe'</c>）と引用符を外した語の列。</summary>
    internal static List<string> Tokenize(string? commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
            return result;
        var line = commandLine.Trim();
        var i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length) break;
            string token;
            if (line[i] is '"' or '\'')
            {
                var quote = line[i];
                var end = line.IndexOf(quote, i + 1);
                token = end < 0 ? line[(i + 1)..] : line[(i + 1)..end];
                i = end < 0 ? line.Length : end + 1;
            }
            else
            {
                var start = i;
                while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
                token = line[start..i];
            }
            if (result.Count == 0 && token == "&")
                continue;
            result.Add(token);
        }
        return result;
    }

    private static string ExecutableName(string token)
    {
        var name = token.Replace('/', '\\');
        var slash = name.LastIndexOf('\\');
        if (slash >= 0) name = name[(slash + 1)..];
        foreach (var ext in (string[])[".exe", ".cmd", ".bat", ".ps1"])
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return name[..^ext.Length];
        }
        return name;
    }
}
