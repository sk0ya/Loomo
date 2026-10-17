using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Editor.Controls.Git;

namespace sk0ya.Loomo.Services;

/// <summary>
/// エディタのガターが比べる相手。<see cref="RepositoryFolder"/> の中のファイルだけを <see cref="BaseRef"/>
/// （分岐点・ブランチ・リビジョンを解決した ref）と比べる。null なら従来どおり HEAD。
/// </summary>
public sealed record EditorGutterBase(string RepositoryFolder, string BaseRef, string Label);

/// <summary>
/// エディタへ渡す <see cref="IEditorGitService"/>。ガターの変更マークだけを、Git パネルと Diff ペインが共有する
/// 比較基準（§24.11）に合わせる——Zed の <c>git.diff_base</c> 相当。「分岐点と比較」にしていれば、ガターも
/// このブランチで入れた変更全部を示す。それ以外（ブランチ名・blame・ステージ等）はエディタ既定の実装のまま。
/// <para>基準で引けなかったとき（別リポジトリ・入れ子のリポジトリ・ref が消えた）は黙って空にせず、
/// HEAD との差分へ戻す——ガターが消えると「変更なし」と嘘をつく。</para>
/// </summary>
public sealed class EditorGutterGitService : IEditorGitService
{
    private readonly GitDiffProvider _inner = new();
    private readonly GitService _git;
    private readonly Func<EditorGutterBase?> _currentBase;

    public EditorGutterGitService(GitService git, Func<EditorGutterBase?> currentBase)
    {
        _git = git;
        _currentBase = currentBase;
    }

    // エディタは背景（Task.Run）からこれを呼ぶので、ここで待ってよい（既定の実装も同期で git を待つ）。
    // 出力の読み取りは GitCommandRunner が専用スレッドで行う（ChildProcessIo）。
    public Dictionary<int, GitLineState> GetDiff(string filePath)
    {
        if (_currentBase() is { } gutterBase && IsInside(gutterBase.RepositoryFolder, filePath))
        {
            var result = _git.GetFileDiffAgainstAsync(gutterBase.RepositoryFolder, gutterBase.BaseRef, filePath)
                .GetAwaiter().GetResult();
            if (result.Success)
                return GitGutterDiffParser.Parse(result.Output);
        }
        return _inner.GetDiff(filePath);
    }

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

    public string? GetBranchName(string repoPath) => _inner.GetBranchName(repoPath);
    public string GetStatusOutput(string repoPath) => _inner.GetStatusOutput(repoPath);
    public string GetDiffOutput(string filePath) => _inner.GetDiffOutput(filePath);
    public string GetLogOutput(string repoPath, int count = 30) => _inner.GetLogOutput(repoPath, count);
    public string GetFileHistoryOutput(string filePath, int? count = null) => _inner.GetFileHistoryOutput(filePath, count);
    public (bool Success, string Output) RunPush(string repoPath) => _inner.RunPush(repoPath);
    public (bool Success, string Output) RunPull(string repoPath) => _inner.RunPull(repoPath);
    public (bool Success, string Output) RunCommit(string filePath, string message) => _inner.RunCommit(filePath, message);
    public (bool Success, string Output) StageHunk(string filePath, int line) => _inner.StageHunk(filePath, line);
    public (bool Success, string Output) UnstageHunk(string filePath, int line) => _inner.UnstageHunk(filePath, line);
    public Dictionary<int, EditorBlameLine> GetBlameLines(string filePath) => _inner.GetBlameLines(filePath);
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
