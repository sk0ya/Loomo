using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Processes;

namespace sk0ya.Loomo.Services;

/// <summary>ファイルの HEAD 時点の本文（「変更行だけ整理」の比べる相手）。
///
/// <para><see cref="GitService"/> の対象リポジトリ（<c>RootPath</c>）とは無関係に、<b>そのファイルを含むリポジトリ</b>へ
/// 直接訊く——マルチルートの追加フォルダーや、対象リポジトリとは別のリポジトリにあるファイルでも正しい相手と比べるため。
/// <c>git -C &lt;ファイルのフォルダー&gt; show HEAD:./&lt;名前&gt;</c> は、そのフォルダーから見た相対パスで解決される。</para>
/// </summary>
public static class GitHeadText
{
    /// <summary>HEAD 時点の本文。Git 管理外・未追跡・まだコミットが無い・git が無い・時間切れは null
    /// （呼び出し側は「全部が変更」として扱う＝Rider と同じく、管理外のファイルはファイル全体が対象）。</summary>
    public static async Task<string?> ReadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (Path.GetDirectoryName(fullPath) is not { } directory || !Directory.Exists(directory))
            return null;

        var startInfo = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(directory);
        startInfo.ArgumentList.Add("--literal-pathspecs");
        startInfo.ArgumentList.Add("show");
        startInfo.ArgumentList.Add("HEAD:./" + Path.GetFileName(fullPath));

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }

        using (process)
        {
            var stdout = ChildProcessIo.ReadToEndAsync(process.StandardOutput, "GitHeadText:stdout");
            var stderr = ChildProcessIo.ReadToEndAsync(process.StandardError, "GitHeadText:stderr");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
            var text = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            return process.ExitCode == 0 ? text : null;
        }
    }
}
