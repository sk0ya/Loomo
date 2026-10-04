using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Processes;

namespace sk0ya.Loomo.Services;

/// <summary>資格情報の取得結果。失敗時は <see cref="Header"/> が null で、<see cref="Message"/> に理由が入る。</summary>
public sealed record AzureDevOpsCredential(AuthenticationHeaderValue? Header, string Message)
{
    public bool Success => Header is not null;
}

/// <summary>
/// Azure DevOps の資格情報を <c>git credential fill</c> から借りる——つまり Git Credential Manager（GCM）に任せる。
/// <para><b>なぜ自前で OAuth しないか</b>——Entra ID で自分のアプリ登録を持つと、企業テナントでは管理者の同意が要る。
/// GCM は Microsoft 側で登録済みのクライアントとして Entra のトークンを取り、保存と更新も受け持つので、
/// git で clone／push できている人ならそのまま Work Items の API も通る。アカウントが複数あっても、
/// GCM は組織ごとに覚えている（<c>git-credential-manager azure-repos bind</c>）ので毎回は聞かれない。</para>
/// <para>GCM が PAT モード（<c>credential.azreposCredentialType=pat</c>）なら PAT が返る。その場合は Basic で送る。
/// どちらが返ったかは中身で判別する（Entra のアクセストークンは JWT）。</para>
/// </summary>
public sealed class GitCredentialTokenProvider
{
    /// <summary>GCM がサインイン画面を出すことがあるので、人が操作し終えるまで待てる長さにする。</summary>
    private static readonly TimeSpan FillTimeout = TimeSpan.FromMinutes(3);

    public async Task<AzureDevOpsCredential> GetAsync(AzureDevOpsOrganization organization, CancellationToken cancellationToken)
    {
        var input = new StringBuilder()
            .Append("protocol=https\n")
            .Append("host=").Append(organization.CredentialHost).Append('\n');
        if (organization.CredentialPath.Length > 0)
            input.Append("path=").Append(organization.CredentialPath).Append('\n');
        input.Append('\n');

        // useHttpPath を明示しないと git が path を落とし、GCM はどの組織の資格情報か分からなくなる。
        var result = await RunGitAsync(input.ToString(), cancellationToken,
            "-c", "credential.useHttpPath=true", "credential", "fill").ConfigureAwait(false);
        if (result.ExitCode != 0)
            return Fail(result.Error.Trim() is { Length: > 0 } error
                ? $"資格情報を取得できませんでした: {FirstLine(error)}"
                : "資格情報を取得できませんでした。ターミナルで一度 git fetch してサインインしてください。");

        var password = ParseValue(result.Output, "password");
        if (string.IsNullOrEmpty(password))
            return Fail("資格情報が空でした。Git Credential Manager が入っているか確認してください。");
        return new AzureDevOpsCredential(CreateHeader(password), "");
    }

    /// <summary>Entra のアクセストークン（JWT）なら Bearer、それ以外（PAT）は Basic。</summary>
    public static AuthenticationHeaderValue CreateHeader(string secret)
        => LooksLikeJwt(secret)
            ? new AuthenticationHeaderValue("Bearer", secret)
            : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + secret)));

    internal static bool LooksLikeJwt(string secret)
    {
        if (!secret.StartsWith("eyJ", StringComparison.Ordinal))
            return false;
        var dots = 0;
        foreach (var c in secret)
            if (c == '.') dots++;
        return dots == 2;
    }

    /// <summary><c>key=value</c> の行から値を取る（git credential の出力形式）。</summary>
    internal static string? ParseValue(string output, string key)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var eq = line.IndexOf('=');
            if (eq > 0 && line.AsSpan(0, eq).SequenceEqual(key))
                return line[(eq + 1)..];
        }
        return null;
    }

    private static AzureDevOpsCredential Fail(string message) => new(null, message);

    private static string FirstLine(string text)
    {
        var newline = text.IndexOf('\n');
        return (newline < 0 ? text : text[..newline]).Trim();
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    /// <summary>標準入力を渡して git を走らせる（<see cref="GitCommandRunner"/> は標準入力を持たないので別に持つ）。</summary>
    private static async Task<ProcessResult> RunGitAsync(string stdin, CancellationToken cancellationToken, params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // 端末での入力待ちはしない（GCM の GUI サインインはこれと無関係に出せる）。
        startInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        Process? process;
        try { process = Process.Start(startInfo); }
        catch (Win32Exception)
        {
            return new ProcessResult(-1, "", "git コマンドが見つかりません。Git for Windows をインストールしてください。");
        }
        if (process is null)
            return new ProcessResult(-1, "", "git を起動できませんでした。");

        using (process)
        {
            // 出力はプールではなく専用スレッドで読む（理由は ChildProcessIo）。
            var stdout = ChildProcessIo.ReadToEndAsync(process.StandardOutput, "git-credential:stdout");
            var stderr = ChildProcessIo.ReadToEndAsync(process.StandardError, "git-credential:stderr");
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var bytes = utf8.GetBytes(stdin);
            await process.StandardInput.BaseStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();

            using var timeout = new CancellationTokenSource(FillTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 既に終了 */ }
                if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    return new ProcessResult(-1, "", "サインインの待ち時間を過ぎました。");
                throw;
            }
            return new ProcessResult(process.ExitCode,
                await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
    }

    /// <summary>フォルダーの git リモート（fetch URL）を並べる。リポジトリでなければ空。</summary>
    public static async Task<IReadOnlyList<string>> GetRemoteUrlsAsync(string folder, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync("", cancellationToken, "-C", folder, "remote", "-v").ConfigureAwait(false);
        var urls = new List<string>();
        if (result.ExitCode != 0)
            return urls;
        foreach (var raw in result.Output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.EndsWith("(fetch)", StringComparison.Ordinal))
                continue;
            var parts = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                urls.Add(parts[1]);
        }
        return urls;
    }
}
