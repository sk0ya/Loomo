using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Editor.Controls.Git;
using sk0ya.Loomo.Core.Harness;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.Services;

/// <summary>
/// エディタのガターが比べる相手。<see cref="RepositoryFolder"/> の中のファイルだけを <see cref="BaseRef"/>
/// （分岐点・ブランチ・リビジョンを解決した ref）と比べる。null なら従来どおり HEAD。
/// </summary>
public sealed record EditorGutterBase(string RepositoryFolder, string BaseRef, string Label);

/// <summary>
/// エディタへ渡す <see cref="IEditorGitService"/>。エディタの git（ガター・blame・ブランチ名・<c>:Git</c> 系・
/// ハンクのステージ）を<b>全部</b> Loomo の <see cref="GitService"/> 経由で動かす。
/// <para>以前はガターだけを差し替え、残りはエディタ既定の <c>GitDiffProvider</c> へ委ねていた。それは
/// <c>.git</c> を<b>フォルダー</b>として探すので、<c>.git</c> がファイルになるワークツリー・サブモジュールでは
/// 「リポジトリの外」とされ、blame もブランチ名も黙って空になっていた。しかも自前で git を起動して
/// 出力をプールのスレッドで同期に読み、stderr は読まずに置いていた（§31.16 の <c>ChildProcessIo</c> の抜け道）。
/// ここではリポジトリの見つけ方を git 自身（ファイルのフォルダーで起動）に任せ、起動と読み取りは
/// <see cref="GitCommandRunner"/> 一本にする。</para>
/// <para>ガターは Git パネルと Diff ペインが共有する比較基準（§24.11）に合わせる——Zed の <c>git.diff_base</c> 相当。
/// 基準で引けなかったとき（別リポジトリ・入れ子のリポジトリ・ref が消えた）は黙って空にせず、
/// HEAD との差分へ戻す——ガターが消えると「変更なし」と嘘をつく。</para>
/// <para>返す文字列（<c>(no changes)</c>・<c>ERROR: …</c> 等）はエディタ既定の実装と同じにしてある——エディタは
/// それをそのまま出力欄やステータスに出す。</para>
/// </summary>
public sealed class EditorGitService : IEditorGitService
{
    private readonly GitService _git;
    private readonly Func<EditorGutterBase?> _currentBase;

    private readonly ClaudeProvenanceIndex? _provenance;

    /// <param name="provenance">渡すと、blame がコミットしていない行を「それを書いた Claude Code の頼みごと」で埋める（§43）。</param>
    public EditorGitService(GitService git, Func<EditorGutterBase?> currentBase, ClaudeProvenanceIndex? provenance = null)
    {
        _git = git;
        _currentBase = currentBase;
        _provenance = provenance;
    }

    // エディタは背景（Task.Run）から呼ぶので、ここで待ってよい（インターフェースが同期）。
    // 出力の読み取りは GitCommandRunner が専用スレッドで行う（ChildProcessIo）。
    private GitCommandResult Run(string folder, params string[] args) =>
        _git.RunInFolderAsync(folder, args).GetAwaiter().GetResult();

    public Dictionary<int, GitLineState> GetDiff(string filePath)
    {
        if (FileFolder(filePath) is not { } folder)
            return [];

        if (_currentBase() is { } gutterBase && IsInside(gutterBase.RepositoryFolder, filePath))
        {
            var result = _git.GetFileDiffAgainstAsync(gutterBase.RepositoryFolder, gutterBase.BaseRef, filePath)
                .GetAwaiter().GetResult();
            if (result.Success)
                return GitGutterDiffParser.Parse(result.Output);
        }

        var head = Run(folder, GitCompareArgs.LiteralPathspecs, "diff", "--no-ext-diff", "HEAD", "--", filePath);
        return head.Success ? GitGutterDiffParser.Parse(head.Output) : [];
    }

    public string? GetBranchName(string repoPath)
    {
        // ガターと一緒に開くたび・保存のたびに呼ばれるので、ルートを先に引かず起動 1 回で済ませる
        // （リポジトリの外なら git が失敗するだけ）。
        if (WorkFolder(repoPath) is not { } folder)
            return null;

        var branch = Run(folder, "branch", "--show-current");
        if (!branch.Success)
            return null;
        if (branch.Output.Trim() is { Length: > 0 } name)
            return name;

        var head = Run(folder, "rev-parse", "--short", "HEAD");
        return head.Success && head.Output.Trim() is { Length: > 0 } hash ? $"HEAD {hash}" : null;
    }

    public string GetStatusOutput(string repoPath)
    {
        if (WorkFolder(repoPath) is null)
            return "(no path)";
        if (RepositoryRoot(repoPath) is not { } root)
            return "(not a git repository)";

        var output = Reported(Run(root, "status", "--short", "--branch", "--untracked-files=all"));
        return string.IsNullOrWhiteSpace(output) ? "## clean" : output;
    }

    public string GetDiffOutput(string filePath)
    {
        if (FileFolder(filePath) is not { } folder || RepositoryRoot(folder) is null)
            return "(no file or not a git repository)";
        var result = Run(folder, GitCompareArgs.LiteralPathspecs, "diff", "--no-ext-diff", "HEAD", "--", filePath);
        return string.IsNullOrEmpty(result.Output) ? "(no changes)" : result.Output;
    }

    public string GetLogOutput(string repoPath, int count = 30)
    {
        if (WorkFolder(repoPath) is null)
            return "(no path)";
        if (RepositoryRoot(repoPath) is not { } root)
            return "(not a git repository)";
        var result = Run(root, "log", "--oneline", $"-{count}");
        return string.IsNullOrEmpty(result.Output) ? "(no commits)" : result.Output;
    }

    public string GetFileHistoryOutput(string filePath, int? count = null)
    {
        if (FileFolder(filePath) is not { } folder || RepositoryRoot(folder) is null)
            return "(no file or not a git repository)";
        // --follow：改名で履歴が切れると、blame が指すコミットが一覧に無くなる。
        var args = new List<string> { "log", "--oneline", "--follow" };
        if (count is > 0)
            args.Add($"-{count}");
        args.Add("--");
        args.Add(filePath);
        var result = Run(folder, args.ToArray());
        return string.IsNullOrEmpty(result.Output) ? "(no commits)" : result.Output;
    }

    public (bool Success, string Output) RunPush(string repoPath) => RunRemote(repoPath, "push");

    public (bool Success, string Output) RunPull(string repoPath) => RunRemote(repoPath, "pull", "--ff-only");

    private (bool Success, string Output) RunRemote(string repoPath, params string[] args)
    {
        if (RepositoryRoot(repoPath) is not { } root)
            return (false, "Not a git repository");

        var result = Run(root, args);
        _git.NotifyRepositoryChanged();
        var output = Reported(result);
        return (result.Success, string.IsNullOrWhiteSpace(output) ? "Already up to date" : output);
    }

    public (bool Success, string Output) RunCommit(string filePath, string message)
    {
        // ファイルが無ければ Git の対象フォルダー（エディタ既定はプロセスのカレントだが、Loomo では意味がない）。
        var root = (FileFolder(filePath) is { } folder ? RepositoryRoot(folder) : null)
            ?? (_git.RootPath is { } target ? RepositoryRoot(target) : null);
        if (root is null)
            return (false, "Not a git repository");

        var result = Run(root, "commit", "-m", message);
        _git.NotifyRepositoryChanged();
        return (result.Success, Reported(result));
    }

    public (bool Success, string Output) StageHunk(string filePath, int line)
    {
        if (FileFolder(filePath) is not { } folder || RepositoryRoot(folder) is null)
            return (false, "Not a git repository");

        var diff = Run(folder, GitCompareArgs.LiteralPathspecs, "diff", "--no-ext-diff", "--unified=3", "--", filePath);
        if (!diff.Success)
            return (false, Reported(diff));
        if (string.IsNullOrWhiteSpace(diff.Output))
            return (false, "No unstaged changes");

        if (!EditorHunkPatch.TryBuildSingleHunkPatch(diff.Output, line, out var patch))
            return (false, "No unstaged hunk at cursor");

        return ApplyCached(folder, patch, reverse: false, "Hunk staged");
    }

    public (bool Success, string Output) UnstageHunk(string filePath, int line)
    {
        if (FileFolder(filePath) is not { } folder || RepositoryRoot(folder) is null)
            return (false, "Not a git repository");

        var diff = Run(folder, GitCompareArgs.LiteralPathspecs, "diff", "--cached", "--no-ext-diff", "--unified=3", "--", filePath);
        if (!diff.Success)
            return (false, Reported(diff));
        if (string.IsNullOrWhiteSpace(diff.Output))
            return (false, "No staged changes");

        // カーソルは作業ツリーの行なので、ステージ済みの差分（インデックス側の行）へ読み替える。
        var worktreeDiff = Run(folder, GitCompareArgs.LiteralPathspecs, "diff", "--no-ext-diff", "--unified=3", "--", filePath);
        if (!worktreeDiff.Success)
            return (false, Reported(worktreeDiff));

        var indexLine = EditorHunkPatch.MapWorktreeLineToIndexLine(worktreeDiff.Output, line);
        if (!EditorHunkPatch.TryBuildSingleHunkPatch(diff.Output, indexLine, out var patch))
            return (false, "No staged hunk at cursor");

        return ApplyCached(folder, patch, reverse: true, "Hunk unstaged");
    }

    // パッチは一時ファイルで渡す（GitPatchService と同じ）。UTF-8・BOM なし——cp932 や BOM だと日本語を含む
    // パッチのバイト列が壊れて apply が失敗する。
    private (bool Success, string Output) ApplyCached(string folder, string patch, bool reverse, string done)
    {
        var patchFile = Path.Combine(Path.GetTempPath(), "loomo-hunk-" + Guid.NewGuid().ToString("N") + ".patch");
        try
        {
            File.WriteAllText(patchFile, patch, new UTF8Encoding(false));
            var result = reverse
                ? Run(folder, "apply", "--cached", "--reverse", patchFile)
                : Run(folder, "apply", "--cached", patchFile);
            _git.NotifyRepositoryChanged();
            var output = Reported(result);
            return (result.Success, result.Success && string.IsNullOrWhiteSpace(output) ? done : output);
        }
        finally
        {
            try { File.Delete(patchFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public Dictionary<int, EditorBlameLine> GetBlameLines(string filePath)
    {
        if (FileFolder(filePath) is not { } folder)
            return [];
        var result = Run(folder, "blame", "--porcelain", "--", filePath);
        var lines = result.Success ? GitBlamePorcelainParser.Parse(result.Output) : [];
        if (_provenance is not null)
            AddClaudeLines(lines, filePath, result.Success ? GitBlamePorcelainParser.ParseUncommitted(result.Output) : UntrackedLines(folder, filePath));
        return lines;
    }

    /// <summary>コミットしていない行を、それを書いた頼みごとで埋める（§43）。git が何も言えない行だけに足す。</summary>
    private void AddClaudeLines(Dictionary<int, EditorBlameLine> lines, string filePath, IReadOnlyDictionary<int, string> uncommitted)
    {
        if (uncommitted.Count == 0)
            return;
        try
        {
            foreach (var (line, write) in _provenance!.Attribute(filePath, uncommitted))
                lines[line] = ClaudeBlame.ToBlameLine(write, _provenance.HandleOf(write));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 記録が読めなくても git の blame はそのまま出す。
        }
    }

    /// <summary>まだ git に載っていないファイルは blame が失敗する。全行がコミットしていない行。</summary>
    private Dictionary<int, string> UntrackedLines(string folder, string filePath)
    {
        const int maxLines = 20_000;
        var result = new Dictionary<int, string>();
        if (!File.Exists(filePath) || !Run(folder, "rev-parse", "--is-inside-work-tree").Success)
            return result;
        try
        {
            var number = 0;
            foreach (var line in File.ReadLines(filePath))
            {
                result[number++] = line;
                if (number >= maxLines)
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }

    /// <summary>エディタ既定の実装と同じ見せ方：成功は stdout（空なら stderr）、失敗は <c>ERROR: …</c>。</summary>
    private static string Reported(GitCommandResult result)
    {
        if (!result.Success)
            return $"ERROR: {(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error).Trim()}";
        var output = result.Output.Trim();
        return output.Length > 0 ? output : result.Error.Trim();
    }

    /// <summary>リポジトリの作業ツリーのルート。ワークツリーならそのワークツリー自身（git が決める）。</summary>
    private string? RepositoryRoot(string repoPath)
    {
        if (WorkFolder(repoPath) is not { } folder)
            return null;
        var result = Run(folder, "rev-parse", "--show-toplevel");
        if (!result.Success || result.Output.Trim() is not { Length: > 0 } top)
            return null;
        try { return Path.GetFullPath(top); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>ファイルならそのフォルダー、フォルダーならそれ自身。どちらでもなければ null。</summary>
    private static string? WorkFolder(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        if (File.Exists(path))
            return Path.GetDirectoryName(path);
        return Directory.Exists(path) ? path : null;
    }

    private static string? FileFolder(string filePath) =>
        !string.IsNullOrEmpty(filePath) && File.Exists(filePath) ? Path.GetDirectoryName(filePath) : null;

    internal static bool IsInside(string folder, string filePath)
    {
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(filePath))
            return false;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(filePath).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

/// <summary>
/// ハンク 1 つだけのパッチを組む（エディタ既定の <c>GitDiffProvider</c> と同じ組み方）。
/// 行はすべて 0 始まり（エディタのカーソル行）。
/// </summary>
public static partial class EditorHunkPatch
{
    [GeneratedRegex(@"^@@ -(?<oldStart>\d+)(?:,(?<oldCount>\d+))? \+(?<newStart>\d+)(?:,(?<newCount>\d+))? @@")]
    private static partial Regex HunkHeaderRegex();

    /// <summary><paramref name="diffOutput"/> のうち、新しい側で <paramref name="zeroBasedLine"/> を含むハンクだけの
    /// パッチ（ファイルヘッダー＋そのハンク）。含むハンクが無ければ false。</summary>
    public static bool TryBuildSingleHunkPatch(string diffOutput, int zeroBasedLine, out string patch)
    {
        patch = "";
        var targetLine = zeroBasedLine + 1;
        var fileHeader = new List<string>();
        List<string>? currentHunk = null;
        var currentContainsTarget = false;

        var diffLines = diffOutput.Split('\n');
        for (var i = 0; i < diffLines.Length; i++)
        {
            var rawLine = diffLines[i];
            if (i == diffLines.Length - 1 && rawLine.Length == 0)
                break;

            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                if (currentHunk != null && currentContainsTarget)
                {
                    patch = BuildPatch(fileHeader, currentHunk);
                    return true;
                }

                currentHunk = [line];
                currentContainsTarget = HunkContainsLine(line, targetLine);
                continue;
            }

            if (currentHunk == null)
                fileHeader.Add(line);
            else
                currentHunk.Add(line);
        }

        if (currentHunk != null && currentContainsTarget)
        {
            patch = BuildPatch(fileHeader, currentHunk);
            return true;
        }

        return false;
    }

    /// <summary>作業ツリーの行 → インデックス側の行（作業ツリーとインデックスの差分 <paramref name="diffOutput"/> で読み替える）。</summary>
    public static int MapWorktreeLineToIndexLine(string diffOutput, int zeroBasedWorktreeLine)
    {
        if (string.IsNullOrWhiteSpace(diffOutput))
            return zeroBasedWorktreeLine;

        var targetNewLine = zeroBasedWorktreeLine + 1;
        var oldLine = 1;
        var newLine = 1;
        var inHunk = false;

        foreach (var rawLine in diffOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var match = HunkHeaderRegex().Match(line);
                if (!match.Success)
                    continue;

                var oldStart = int.Parse(match.Groups["oldStart"].Value);
                var newStart = int.Parse(match.Groups["newStart"].Value);

                if (targetNewLine < newStart)
                    return Math.Max(0, oldLine + (targetNewLine - newLine) - 1);

                oldLine = oldStart;
                newLine = newStart;
                inHunk = true;
                continue;
            }

            if (!inHunk || line.Length == 0)
                continue;

            switch (line[0])
            {
                case ' ':
                    if (newLine == targetNewLine)
                        return Math.Max(0, oldLine - 1);
                    oldLine++;
                    newLine++;
                    break;
                case '+':
                    if (newLine == targetNewLine)
                        return Math.Max(0, oldLine - 1);
                    newLine++;
                    break;
                case '-':
                    oldLine++;
                    break;
            }
        }

        return Math.Max(0, oldLine + (targetNewLine - newLine) - 1);
    }

    private static string BuildPatch(List<string> fileHeader, List<string> hunk)
    {
        var lines = new List<string>(fileHeader.Count + hunk.Count);
        lines.AddRange(fileHeader.Where(l => l.Length > 0));
        lines.AddRange(hunk);
        return string.Join('\n', lines) + "\n";
    }

    private static bool HunkContainsLine(string hunkHeader, int oneBasedLine)
    {
        var match = HunkHeaderRegex().Match(hunkHeader);
        if (!match.Success) return false;

        var newStart = int.Parse(match.Groups["newStart"].Value);
        var newCount = match.Groups["newCount"].Success ? int.Parse(match.Groups["newCount"].Value) : 1;

        var last = newCount == 0 ? newStart : newStart + newCount - 1;
        return oneBasedLine >= newStart && oneBasedLine <= last;
    }
}

/// <summary>
/// <c>git blame --porcelain</c> → 行（0 始まり）ごとのコミット情報（エディタ既定の <c>GitDiffProvider</c> と同じ読み方）。
/// 各行はヘッダー「&lt;40桁hash&gt; &lt;元行&gt; &lt;結果行&gt; [&lt;行数&gt;]」＋（コミット初出時のみ）author/summary 等の
/// メタ行＋タブ始まりの本文行。メタは初出時にしか出ないのでハッシュごとに覚えて後の行にも付ける。
/// 未コミット行（hash が全て 0）は含めない。
/// </summary>
public static class GitBlamePorcelainParser
{
    private sealed class CommitMeta
    {
        public string Author = "";
        public string Date = "";
        public string Summary = "";
    }

    public static Dictionary<int, EditorBlameLine> Parse(string blameOutput)
    {
        var result = new Dictionary<int, EditorBlameLine>();
        if (string.IsNullOrEmpty(blameOutput))
            return result;

        var metas = new Dictionary<string, CommitMeta>();
        string? currentHash = null;
        CommitMeta? currentMeta = null;
        var resultLine = -1;
        var origLine = 0;

        foreach (var raw in blameOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            if (char.IsAsciiHexDigit(line[0]) && line.Length > 40 && line[40] == ' ')
            {
                var parts = line.Split(' ');
                currentHash = parts[0];
                origLine = parts.Length > 1 && int.TryParse(parts[1], out var ol) ? ol : 0;
                resultLine = parts.Length > 2 && int.TryParse(parts[2], out var rl) ? rl - 1 : -1;
                if (!metas.TryGetValue(currentHash, out currentMeta))
                    metas[currentHash] = currentMeta = new CommitMeta();
            }
            else if (line.StartsWith("author ", StringComparison.Ordinal) && currentMeta != null)
            {
                currentMeta.Author = line[7..].Trim();
            }
            else if (line.StartsWith("author-time ", StringComparison.Ordinal) && currentMeta != null)
            {
                if (long.TryParse(line[12..], out var ts))
                    currentMeta.Date = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime.ToString("yyyy-MM-dd");
            }
            else if (line.StartsWith("summary ", StringComparison.Ordinal) && currentMeta != null)
            {
                currentMeta.Summary = line[8..].Trim();
            }
            else if (line[0] == '\t' && currentHash != null && currentMeta != null && resultLine >= 0)
            {
                if (!currentHash.All(c => c == '0'))
                    result[resultLine] = new EditorBlameLine(
                        currentHash[..Math.Min(7, currentHash.Length)],
                        currentMeta.Author, currentMeta.Date, currentMeta.Summary, origLine);
            }
        }

        return result;
    }

    /// <summary>コミットしていない行（hash が全て 0）だけを、0 始まりの行番号 → 中身で返す（§43 の出自の入力）。</summary>
    public static Dictionary<int, string> ParseUncommitted(string blameOutput)
    {
        var result = new Dictionary<int, string>();
        if (string.IsNullOrEmpty(blameOutput))
            return result;
        var uncommitted = false;
        var resultLine = -1;
        foreach (var raw in blameOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line[0] == '\t')
            {
                if (uncommitted && resultLine >= 0)
                    result[resultLine] = line[1..];
                resultLine = -1;
            }
            else if (char.IsAsciiHexDigit(line[0]) && line.Length > 40 && line[40] == ' ')
            {
                var parts = line.Split(' ');
                uncommitted = parts[0].All(c => c == '0');
                resultLine = parts.Length > 2 && int.TryParse(parts[2], out var rl) ? rl - 1 : -1;
            }
        }
        return result;
    }
}

/// <summary>
/// 出自（§43）をエディタの blame の行に載せる。エディタの <see cref="EditorBlameLine"/> は文字列と整数 1 つしか運べない
/// ので、ハッシュの欄に印（<see cref="Marker"/>）、元の行番号の欄に索引の取っ手（<see cref="ClaudeProvenanceIndex.HandleOf"/>）を入れる。
/// </summary>
public static class ClaudeBlame
{
    /// <summary>ハッシュの欄に入れる印。16 進ではないので、コミットとして扱う経路（リンク・差分）には乗らない。</summary>
    public const string Marker = "Claude";

    public static bool IsClaude(EditorBlameLine blame) => blame.CommitHash == Marker;

    public static EditorBlameLine ToBlameLine(ClaudeWrite write, int handle)
        => new(Marker, write.FromSubagent ? "サブエージェント" : "頼みごと",
            write.At?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "", write.PromptHead, handle);
}

/// <summary>
/// unified diff → ガターの行ごとの状態（0 始まりの新しい側の行）。エディタ既定の
/// <c>GitDiffProvider</c> と同じ読み方（削除の直後に足された行は「変更」、足されずに消えた行は次の行に「削除」）。
/// </summary>
public static partial class GitGutterDiffParser
{
    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeaderRegex();

    public static Dictionary<int, GitLineState> Parse(string diffOutput)
    {
        var result = new Dictionary<int, GitLineState>();
        if (string.IsNullOrEmpty(diffOutput))
            return result;

        int newLine = -1;
        int pendingDeletes = 0;
        bool inHeader = true;

        void FlushDeleted()
        {
            if (pendingDeletes <= 0 || newLine < 0) return;
            result.TryAdd(newLine, GitLineState.Deleted);
            pendingDeletes = 0;
        }

        foreach (var raw in diffOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                FlushDeleted();
                var match = HunkHeaderRegex().Match(line);
                if (match.Success)
                {
                    newLine = int.Parse(match.Groups[1].Value) - 1;
                    pendingDeletes = 0;
                    inHeader = false;
                }
                continue;
            }

            if (inHeader || newLine < 0 || line.Length == 0) continue;

            switch (line[0])
            {
                case '+':
                    result[newLine] = pendingDeletes > 0 ? GitLineState.Modified : GitLineState.Added;
                    if (pendingDeletes > 0) pendingDeletes--;
                    newLine++;
                    break;
                case '-':
                    pendingDeletes++;
                    break;
                case ' ':
                    FlushDeleted();
                    newLine++;
                    break;
            }
        }

        FlushDeleted();
        return result;
    }
}
