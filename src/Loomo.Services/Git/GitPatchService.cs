using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services;

/// <summary>パッチをどう当てられるか（§24.38.1）。</summary>
public enum GitPatchApplyMode
{
    /// <summary>当てられない（壊れたパッチ・別のファイル向け・衝突を解けない）。</summary>
    None,
    /// <summary>そのまま作業ツリーへ当たる（ステージは触らない）。</summary>
    Direct,
    /// <summary>そのままでは当たらないが 3-way なら当たる（当たった変更はステージされ、衝突には印が付く）。</summary>
    ThreeWay,
}

/// <summary>当てる前に見せるパッチの中身。<see cref="Files"/> はパッチの中のパス → 増減の行数。</summary>
public sealed record GitPatchPreview(
    IReadOnlyDictionary<string, GitLineStat> Files, GitPatchApplyMode Mode, string? Error, string PatchFile)
{
    /// <summary><see cref="PatchFile"/> がクリップボードの本文を書き出した一時ファイルか（使い終わったら消す）。</summary>
    public bool IsTemporary { get; init; }

    /// <summary>一時ファイルなら消す。人間のファイルは消さない。</summary>
    public void Discard()
    {
        if (!IsTemporary) return;
        try { File.Delete(PatchFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// 外から来たパッチ（クリップボード・<c>.patch</c> ファイル）を作業ツリーへ当てる（§24.38.1・Fork 2.17 の「Ctrl+V to apply patch」）。
/// まず素の <c>git apply</c>（作業ツリーだけ・ステージは触らない）で当たるかを確かめ、当たらなければ <c>--3way</c>
/// （ベースのオブジェクトがあれば 3-way マージで当て、衝突には印を付ける。<b>当たった変更はステージされる</b>）。
/// 当てる前に <c>--check</c> で確かめるので、半分だけ当たって作業ツリーが壊れることはない。
/// </summary>
public sealed class GitPatchService
{
    private readonly GitCommandRunner _runner;
    private readonly GitMutationExecutor _mutations;

    public GitPatchService(GitCommandRunner runner, GitMutationExecutor mutations)
    {
        _runner = runner;
        _mutations = mutations;
    }

    /// <summary>クリップボードなどの本文を一時ファイルに書き出して確かめる。改行が CRLF の本文で当たらなければ
    /// LF にそろえた版でも確かめる（Windows のクリップボードは改行を CRLF に変えることがある）。</summary>
    public async Task<GitPatchPreview> PreviewTextAsync(string patchText)
    {
        var text = patchText.EndsWith('\n') ? patchText : patchText + "\n";
        var preview = await PreviewFileAsync(WriteTemp(text)).ConfigureAwait(false) with { IsTemporary = true };
        if (preview.Mode != GitPatchApplyMode.None || !text.Contains("\r\n", StringComparison.Ordinal))
            return preview;
        var normalized = await PreviewFileAsync(WriteTemp(text.Replace("\r\n", "\n"))).ConfigureAwait(false) with { IsTemporary = true };
        if (normalized.Mode == GitPatchApplyMode.None)
        {
            normalized.Discard();
            return preview;
        }
        preview.Discard();
        return normalized;
    }

    public async Task<GitPatchPreview> PreviewFileAsync(string patchFile)
    {
        var numstat = await _runner.RunAsync("apply", "--numstat", "-z", patchFile).ConfigureAwait(false);
        if (!numstat.Success)
            return new GitPatchPreview(new Dictionary<string, GitLineStat>(), GitPatchApplyMode.None,
                FirstLine(numstat.Message) ?? "パッチとして読めません。", patchFile);
        var files = GitNumstatParser.Parse(numstat.Output);
        if (files.Count == 0)
            return new GitPatchPreview(files, GitPatchApplyMode.None, "パッチに変更が含まれていません。", patchFile);

        var direct = await _runner.RunAsync("apply", "--check", "--whitespace=nowarn", patchFile).ConfigureAwait(false);
        if (direct.Success)
            return new GitPatchPreview(files, GitPatchApplyMode.Direct, null, patchFile);
        var threeWay = await _runner.RunAsync("apply", "--check", "--3way", "--whitespace=nowarn", patchFile).ConfigureAwait(false);
        return threeWay.Success
            ? new GitPatchPreview(files, GitPatchApplyMode.ThreeWay, null, patchFile)
            : new GitPatchPreview(files, GitPatchApplyMode.None, FirstLine(direct.Message) ?? "パッチが当たりません。", patchFile);
    }

    /// <summary>確かめた方式で当てる。</summary>
    public Task<GitCommandResult> ApplyAsync(GitPatchPreview preview)
        => preview.Mode switch
        {
            GitPatchApplyMode.Direct => _mutations.ExecuteAsync("apply", "--whitespace=nowarn", preview.PatchFile),
            GitPatchApplyMode.ThreeWay => _mutations.ExecuteAsync("apply", "--3way", "--whitespace=nowarn", preview.PatchFile),
            _ => Task.FromResult(new GitCommandResult(-1, "", preview.Error ?? "パッチが当たりません。")),
        };

    /// <summary>
    /// 作業ツリーの未コミットの変更（HEAD との差。ステージ済みか否かは問わず、未追跡・削除・バイナリも含む）を
    /// <paramref name="paths"/> に絞ってパッチとして <paramref name="outputPath"/> へ書く（§24.79）。
    /// 未追跡のファイルは素の <c>git diff</c> に出ないので、<b>使い捨てのインデックス</b>（<c>GIT_INDEX_FILE</c>）へ
    /// HEAD を読み込んで <c>add -A</c> し、<c>diff --cached</c> を取る——人のインデックス（ステージ）には触らない。
    /// git に直接書かせる（<c>--output</c>）のはスタッシュの保存と同じ理由（UTF-8 でない中身を化けさせない）。
    /// <paramref name="root"/> を渡せばそのフォルダー（別のワークツリー）で取る。
    /// </summary>
    public async Task<GitCommandResult> WriteWorkingPatchAsync(
        IReadOnlyList<string> paths, string outputPath, string? root = null)
    {
        if (paths.Count == 0)
            return new GitCommandResult(-1, "", "パッチにするファイルがありません。");
        var index = Path.Combine(Path.GetTempPath(), "loomo-index-" + Guid.NewGuid().ToString("N"));
        var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
        try
        {
            // 生まれたばかりのリポジトリ（HEAD がまだ無い）では空から始める。
            var read = await RunAsync(root, env, "read-tree", "HEAD").ConfigureAwait(false);
            if (!read.Success)
                read = await RunAsync(root, env, "read-tree", "--empty").ConfigureAwait(false);
            if (!read.Success)
                return read;
            var add = await RunAsync(root, env, ["add", "-A", "--", .. paths]).ConfigureAwait(false);
            if (!add.Success)
                return add;
            return await RunAsync(root, env,
                ["diff", "--cached", "--binary", "--no-color", "--no-ext-diff", "--no-textconv",
                 $"--output={outputPath}", "--", .. paths]).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(index); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary><see cref="WriteWorkingPatchAsync"/> を一時ファイルに書いて本文で返す（クリップボード用）。変更が無ければ空文字。
    /// 失敗は <see cref="GitCommandResult"/> の理由で返す。</summary>
    public async Task<(string? Text, GitCommandResult Result)> GetWorkingPatchTextAsync(IReadOnlyList<string> paths)
    {
        var path = Path.Combine(Path.GetTempPath(), "loomo-patch-" + Guid.NewGuid().ToString("N") + ".patch");
        try
        {
            var result = await WriteWorkingPatchAsync(paths, path).ConfigureAwait(false);
            return result.Success && File.Exists(path) ? (await File.ReadAllTextAsync(path).ConfigureAwait(false), result) : (null, result);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private Task<GitCommandResult> RunAsync(string? root, IReadOnlyDictionary<string, string> env, params string[] args)
        => root is null
            ? _runner.RunAsync(env, args)
            : _runner.RunInAsync(root, env, null, System.Threading.CancellationToken.None, args);

    // 本文はバイト列のまま書く（BOM を付けない）。BOM が先頭に付くと git はパッチの始まりを読めない。
    private static string WriteTemp(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), "loomo-patch-" + Guid.NewGuid().ToString("N") + ".patch");
        File.WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text));
        return path;
    }

    private static string? FirstLine(string message)
        => message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
