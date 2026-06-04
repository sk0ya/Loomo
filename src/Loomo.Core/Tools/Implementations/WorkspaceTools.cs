using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Core.Tools.Implementations;

internal static class ArgHelper
{
    public static string GetString(this JsonElement args, string name, string fallback = "")
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;

    public static int GetInt(this JsonElement args, string name, int fallback)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt32(out var result)
            ? result
            : fallback;

    public static bool GetBool(this JsonElement args, string name, bool fallback)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(name, out var v)
           && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : fallback;
}

/// <summary>フォルダツリーを一括ダンプする。</summary>
public sealed class GetProjectTreeTool : IAgentTool
{
    private readonly IWorkspaceService _workspace;
    public GetProjectTreeTool(IWorkspaceService workspace) => _workspace = workspace;

    public string Name => "get_project_tree";
    public bool RequiresApproval => false;

    public ToolDefinition Definition => new(
        Name,
        "起点（省略時はワークスペースルート）配下のフォルダ/ファイルをインデント付きツリーで一度にまとめて返す。"
        + "bin/obj/.git/node_modules などの生成物と、.gitignore で無視されている項目は除外する。1階層目は常に全て表示し、"
        + "深い階層は項目数の上限に達するまで浅い順（幅優先）に展開する。展開しきれなかったフォルダは末尾に … が付く。"
        + "フォルダ構成の全体像を1回で把握するのに使う。",
        ToolDefinition.ObjectSchema(
            ("path", "string", "起点ディレクトリの絶対/相対パス。省略時はワークスペースルート。", false),
            ("max_entries", "integer", "2階層目以降に表示する最大エントリ数。省略時150、最大5000。", false)));

    public string DescribeInvocation(JsonElement args) => $"ツリー: {args.GetString("path", "(ルート)")}";

    public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var start = _workspace.ResolvePath(args.GetString("path"));
        if (!Directory.Exists(start)) return Task.FromResult(ToolResult.Error($"ディレクトリが存在しません: {start}"));

        var maxEntries = Math.Max(1, Math.Min(5000, args.GetInt("max_entries", 150)));

        // .gitignore で無視される項目を除くため、起点が git 管理下なら check-ignore を使う。
        var ignore = GitIgnoreChecker.Create(start);

        // 1階層目は無条件で全て、2階層目以降は budget が尽きるまで幅優先（浅い順）で展開する。
        // ツリー全体を再帰探索せず、表示するノードだけを訪れるので大きなルートでも軽い。
        var root = new TreeNode("", isDir: true, start);
        var queue = new Queue<TreeNode>();
        foreach (var child in EnumerateChildren(start, ignore, ct))
        {
            root.Children.Add(child);
            if (child.IsDir) queue.Enqueue(child);
        }

        var budget = maxEntries;
        var truncated = false;
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = queue.Dequeue();
            if (budget <= 0)
            {
                // 上限に達したので、このフォルダ以降は展開しない（中身の有無は確認しない）。
                dir.ChildrenOmitted = true;
                truncated = true;
                continue;
            }

            dir.Expanded = true;
            foreach (var child in EnumerateChildren(dir.FullPath, ignore, ct))
            {
                if (budget <= 0)
                {
                    dir.ChildrenOmitted = true;
                    truncated = true;
                    break;
                }

                budget--;
                dir.Children.Add(child);
                if (child.IsDir) queue.Enqueue(child);
            }
        }

        if (root.Children.Count == 0) return Task.FromResult(ToolResult.Ok("(空)"));

        var sb = new StringBuilder();
        Render(root, sb, 0);
        if (truncated)
            sb.Append("…(項目上限に達したため一部フォルダは未展開。max_entries を増やすか path で起点を絞る)").Append('\n');
        return Task.FromResult(ToolResult.Ok(sb.ToString()));
    }

    /// <summary>1ディレクトリ直下の子（生成物・.gitignore対象・シンボリックリンクは除外）をフォルダ→ファイルの名前昇順で返す。</summary>
    private static IEnumerable<TreeNode> EnumerateChildren(string dir, GitIgnoreChecker ignore, CancellationToken ct)
    {
        string[] subdirs;
        string[] files;
        try
        {
            subdirs = Directory.GetDirectories(dir);
            files = Directory.GetFiles(dir);
        }
        catch (UnauthorizedAccessException) { yield break; }
        catch (DirectoryNotFoundException) { yield break; }
        catch (IOException) { yield break; }

        // gitignore 判定は dir 直下の子をまとめて 1 回だけ git に問い合わせる。
        var ignored = ignore.GetIgnored(dir, subdirs, files, ct);

        foreach (var d in subdirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (!FindFilesTool.ShouldSkipDirectory(d) && !ignored.Contains(d))
                yield return new TreeNode(Path.GetFileName(d), isDir: true, d);
        }

        foreach (var f in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (!FindFilesTool.ShouldSkipFile(f) && !ignored.Contains(f))
                yield return new TreeNode(Path.GetFileName(f), isDir: false, f);
        }
    }

    private static void Render(TreeNode node, StringBuilder sb, int depth)
    {
        foreach (var child in node.Children)
        {
            sb.Append(' ', depth * 2).Append(child.Name);
            if (child.IsDir)
            {
                sb.Append('/');
                // 未展開、または上限で途中まで しか出せなかったフォルダは … で示す。
                if (!child.Expanded || child.ChildrenOmitted) sb.Append(" …");
            }

            sb.Append('\n');
            if (child.IsDir) Render(child, sb, depth + 1);
        }
    }

    /// <summary>表示対象として確定したツリーの1ノード（必要なフォルダだけ遅延展開する）。</summary>
    private sealed class TreeNode
    {
        public TreeNode(string name, bool isDir, string fullPath)
        {
            Name = name;
            IsDir = isDir;
            FullPath = fullPath;
        }

        public string Name { get; }
        public bool IsDir { get; }
        public string FullPath { get; }
        public List<TreeNode> Children { get; } = new();

        /// <summary>このフォルダの中身を列挙したか（未展開なら子は未知）。</summary>
        public bool Expanded { get; set; }

        /// <summary>上限により子の一部または全部を出せなかったか。</summary>
        public bool ChildrenOmitted { get; set; }
    }
}

/// <summary>
/// 起点が git 管理下のとき、ディレクトリ直下の子のうち .gitignore で無視される
/// ものを <c>git check-ignore</c> でまとめて判定する。git が無い・リポジトリ外なら
/// 以降は問い合わせず、常に「無視なし」を返す。
/// </summary>
internal sealed class GitIgnoreChecker
{
    private static readonly HashSet<string> None = new(StringComparer.OrdinalIgnoreCase);

    private bool _enabled;

    private GitIgnoreChecker(bool enabled) => _enabled = enabled;

    public static GitIgnoreChecker Create(string startDir)
    {
        if (!Directory.Exists(startDir)) return new GitIgnoreChecker(false);
        try
        {
            var (exit, output) = RunGit(startDir, stdin: null, "rev-parse", "--is-inside-work-tree");
            return new GitIgnoreChecker(exit == 0 && output.Trim() == "true");
        }
        catch
        {
            return new GitIgnoreChecker(false);
        }
    }

    /// <summary>dir 直下の子フルパスのうち、.gitignore で無視されるものの集合を返す。</summary>
    public ISet<string> GetIgnored(string dir, string[] subdirs, string[] files, CancellationToken ct)
    {
        if (!_enabled || (subdirs.Length == 0 && files.Length == 0)) return None;

        // 子名のみを cwd=dir として渡す（git は親・ルートの .gitignore も加味して判定する）。
        var names = new List<string>(subdirs.Length + files.Length);
        foreach (var d in subdirs) names.Add(Path.GetFileName(d));
        foreach (var f in files) names.Add(Path.GetFileName(f));

        try
        {
            ct.ThrowIfCancellationRequested();
            var stdin = string.Join('\0', names) + '\0';
            var (exit, output) = RunGit(dir, stdin, "check-ignore", "-z", "--stdin");

            // 128 = リポジトリ外/エラー → 以降は問い合わせない。0=一部無視, 1=無視なし。
            if (exit == 128) { _enabled = false; return None; }
            if (exit is not 0 and not 1) return None;

            var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                ignored.Add(Path.Combine(dir, name));
            return ignored;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return None;
        }
    }

    private static (int ExitCode, string Output) RunGit(string workingDir, string? stdin, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi);
        if (process is null) return (-1, string.Empty);

        // stdin を書き終える前から stdout を非同期に読み出す。先に読み手を回しておかないと、
        // 大量の子（数千件）で stdin と stdout の両パイプが同時に詰まりデッドロックする。
        var outputTask = process.StandardOutput.ReadToEndAsync();

        if (stdin is not null)
        {
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
        }

        // git が応答しない場合に無限ハングしないよう、上限時間で打ち切って kill する。
        if (!process.WaitForExit(5000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 既に終了済み */ }
            return (-1, string.Empty);
        }

        return (process.ExitCode, outputTask.GetAwaiter().GetResult());
    }
}

/// <summary>ファイル読み取り。</summary>
public sealed class ReadFileTool : IAgentTool
{
    private readonly IWorkspaceService _workspace;
    public ReadFileTool(IWorkspaceService workspace) => _workspace = workspace;

    public string Name => "read_file";
    public bool RequiresApproval => false;

    public ToolDefinition Definition => new(
        Name,
        "ファイルの内容を読み取る。",
        ToolDefinition.ObjectSchema(
            ("path", "string", "読み取るファイルのパス。", true)));

    public string DescribeInvocation(JsonElement args) => $"読取: {args.GetString("path")}";

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var path = args.GetString("path");
        if (string.IsNullOrWhiteSpace(path)) return ToolResult.Error("path は必須です。");
        var content = await _workspace.ReadFileAsync(path);
        return ToolResult.Ok(content);
    }
}

/// <summary>ワークスペース内のファイル名検索。</summary>
public sealed class FindFilesTool : IAgentTool
{
    private readonly IWorkspaceService _workspace;
    public FindFilesTool(IWorkspaceService workspace) => _workspace = workspace;

    public string Name => "find_files";
    public bool RequiresApproval => false;

    public ToolDefinition Definition => new(
        Name,
        "ワークスペース内でファイル名をワイルドカード検索する。bin/obj/.git などの生成物フォルダは除外する。",
        ToolDefinition.ObjectSchema(
            ("pattern", "string", "検索するファイル名パターン。例: *.cs, *ViewModel*, Roadmap.md", true),
            ("path", "string", "検索を開始するディレクトリの絶対/相対パス。省略時はワークスペースルート。", false),
            ("max_results", "integer", "返す最大件数。省略時100、最大500。", false)));

    public string DescribeInvocation(JsonElement args) => $"ファイル検索: {args.GetString("pattern")}";

    public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var pattern = args.GetString("pattern");
        if (string.IsNullOrWhiteSpace(pattern)) return Task.FromResult(ToolResult.Error("pattern は必須です。"));

        var start = _workspace.ResolvePath(args.GetString("path"));
        if (!Directory.Exists(start)) return Task.FromResult(ToolResult.Error($"ディレクトリが存在しません: {start}"));

        var maxResults = Clamp(args.GetInt("max_results", 100), 1, 500);
        var matcher = Wildcard(pattern);
        var results = new List<string>();

        foreach (var file in EnumerateWorkspaceFiles(start, ct))
        {
            if (!matcher.IsMatch(Path.GetFileName(file))) continue;
            results.Add(ToDisplayPath(_workspace.RootPath, file));
            if (results.Count >= maxResults) break;
        }

        return Task.FromResult(ToolResult.Ok(results.Count == 0 ? "(一致なし)" : string.Join(Environment.NewLine, results)));
    }

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));

    private static Regex Wildcard(string pattern)
        => new("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static IEnumerable<string> EnumerateWorkspaceFiles(string start, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(start);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            string[] subdirs;
            string[] files;

            try
            {
                subdirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var subdir in subdirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (!ShouldSkipDirectory(subdir)) pending.Push(subdir);
            }

            foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if (ShouldSkipFile(file)) continue;
                yield return file;
            }
        }
    }

    internal static bool ShouldSkipDirectory(string path)
    {
        var name = Path.GetFileName(path);
        if (IsReparsePoint(path)) return true;

        return name.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".vs", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".idea", StringComparison.OrdinalIgnoreCase)
            || name.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || name.Equals("packages", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldSkipFile(string path) => IsReparsePoint(path);

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
    }

    internal static string ToDisplayPath(string? rootPath, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return fullPath;
        var relative = Path.GetRelativePath(rootPath, fullPath);
        return relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? fullPath
            : relative;
    }
}

/// <summary>ワークスペース内の全文検索。</summary>
public sealed class SearchFilesTool : IAgentTool
{
    private readonly IWorkspaceService _workspace;
    public SearchFilesTool(IWorkspaceService workspace) => _workspace = workspace;

    public string Name => "search_files";
    public bool RequiresApproval => false;

    public ToolDefinition Definition => new(
        Name,
        "ワークスペース内のテキストファイルを全文検索し、path:line:text 形式で一致箇所を返す。",
        ToolDefinition.ObjectSchema(
            ("query", "string", "検索文字列。正規表現ではなくリテラル文字列として扱う。", true),
            ("path", "string", "検索を開始するディレクトリの絶対/相対パス。省略時はワークスペースルート。", false),
            ("file_pattern", "string", "対象ファイル名パターン。省略時は *。例: *.cs", false),
            ("case_sensitive", "boolean", "大文字小文字を区別するか。省略時 false。", false),
            ("max_results", "integer", "返す最大一致件数。省略時100、最大500。", false),
            ("max_files", "integer", "検索対象として読む最大ファイル数。省略時20000、最大100000。", false),
            ("max_file_bytes", "integer", "検索対象にする最大ファイルサイズ。省略時1048576、最大10485760。", false)));

    public string DescribeInvocation(JsonElement args) => $"全文検索: {args.GetString("query")}";

    public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var query = args.GetString("query");
        if (string.IsNullOrEmpty(query)) return Task.FromResult(ToolResult.Error("query は必須です。"));

        var start = _workspace.ResolvePath(args.GetString("path"));
        if (!Directory.Exists(start)) return Task.FromResult(ToolResult.Error($"ディレクトリが存在しません: {start}"));

        var filePattern = args.GetString("file_pattern", "*");
        var maxResults = Math.Max(1, Math.Min(500, args.GetInt("max_results", 100)));
        var maxFiles = Math.Max(1, Math.Min(100_000, args.GetInt("max_files", 20_000)));
        var maxFileBytes = Math.Max(1, Math.Min(10 * 1024 * 1024, args.GetInt("max_file_bytes", 1024 * 1024)));
        var comparison = args.GetBool("case_sensitive", false)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        var matcher = new Regex(
            "^" + Regex.Escape(filePattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var results = new List<string>();
        var inspectedFiles = 0;
        var hitFileLimit = false;

        foreach (var file in FindFilesTool.EnumerateWorkspaceFiles(start, ct))
        {
            if (!matcher.IsMatch(Path.GetFileName(file))) continue;

            inspectedFiles++;
            if (inspectedFiles > maxFiles)
            {
                hitFileLimit = true;
                break;
            }

            AddMatches(file, query, comparison, maxResults, maxFileBytes, results, ct);
            if (results.Count >= maxResults) break;
        }

        if (hitFileLimit)
        {
            results.Add($"(検索上限に達しました: max_files={maxFiles})");
        }

        return Task.FromResult(ToolResult.Ok(results.Count == 0 ? "(一致なし)" : string.Join(Environment.NewLine, results)));
    }

    private void AddMatches(
        string file,
        string query,
        StringComparison comparison,
        int maxResults,
        int maxFileBytes,
        List<string> results,
        CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.Length > maxFileBytes) return;

            var lineNumber = 0;
            using var stream = File.OpenRead(file);
            if (ContainsNullByte(stream)) return;
            stream.Position = 0;
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                ct.ThrowIfCancellationRequested();
                lineNumber++;
                if (!line.Contains(query, comparison)) continue;

                var displayPath = FindFilesTool.ToDisplayPath(_workspace.RootPath, file);
                results.Add($"{displayPath}:{lineNumber}:{line.Trim()}");
                if (results.Count >= maxResults) return;
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
        catch (System.Text.DecoderFallbackException)
        {
        }
    }

    private static bool ContainsNullByte(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[512];
        var read = stream.Read(buffer);
        for (var i = 0; i < read; i++)
        {
            if (buffer[i] == 0) return true;
        }

        return false;
    }
}

/// <summary>FolderTree の現在選択を取得。</summary>
public sealed class GetSelectionTool : IAgentTool
{
    private readonly IWorkspaceService _workspace;
    public GetSelectionTool(IWorkspaceService workspace) => _workspace = workspace;

    public string Name => "get_selection";
    public bool RequiresApproval => false;

    public ToolDefinition Definition => new(
        Name,
        "FolderTree で現在選択中のパスと、ワークスペースルートを返す。",
        ToolDefinition.ObjectSchema());

    public string DescribeInvocation(JsonElement args) => "選択取得";

    public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
        => Task.FromResult(ToolResult.Ok(
            $"root: {_workspace.RootPath ?? "(未設定)"}\nselected: {_workspace.SelectedPath ?? "(なし)"}"));
}
