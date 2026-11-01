using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Git;
using sk0ya.Loomo.Core.Processes;

namespace sk0ya.Loomo.Services;

/// <summary>覚えさせた結果。<paramref name="Message"/> は人に見せる一文。</summary>
public sealed record GitCredentialRegistrationResult(bool Success, string Message);

/// <summary>「次から聞かれないようにする」を実際に行う（§24.101）。</summary>
public interface IGitCredentialRegistrar
{
    /// <summary>この覚えさせ方がこの PC で使えるか（GCM が入っている・Windows の OpenSSH がある）。使えなければ入力欄にチェックを出さない。</summary>
    bool CanRemember(GitCredentialRemember remember);

    /// <param name="userName">HTTPS のユーザー名（問いに無ければ、同じ git の先の問いで答えられたもの）。</param>
    /// <param name="secret">パスワード・合言葉。どこにも書かず、正規の置き場へ標準入力か askpass で渡す。</param>
    /// <param name="answeringEnvironment">ssh-add が合言葉を問うたら、受け口が人に聞かずに <paramref name="secret"/> を返すための環境。</param>
    Task<GitCredentialRegistrationResult> RememberAsync(GitCredentialRemember remember, string? userName, string secret,
        IReadOnlyDictionary<string, string>? answeringEnvironment, CancellationToken cancellationToken = default);
}

/// <summary>Windows の ssh-agent サービス。起こすには管理者の確認（UAC）が要るので、テストでは差し替える。</summary>
public interface ISshAgentService
{
    /// <summary>動いているか。サービスが無ければ null（Windows の OpenSSH が入っていない）。</summary>
    Task<bool?> IsRunningAsync();

    /// <summary>自動起動にして起こす。人が管理者の確認を断ったら <see cref="GitCredentialRegistrationResult.Success"/> は false。</summary>
    Task<GitCredentialRegistrationResult> StartAsync();
}

/// <summary>
/// HTTPS は Git Credential Manager へ、SSH の鍵の合言葉は Windows の ssh-agent へ（§24.101）。Loomo 自身は答えを持たない。
/// <list type="bullet">
/// <item>HTTPS：その接続先に効く資格情報ヘルパーが無ければ、git 全体の設定に <c>credential.&lt;接続先&gt;.helper</c> を足し
/// （ほかの接続先には触らない）、<c>git credential approve</c> で GCM に保存させる。パスワードは標準入力で渡し、引数に載せない。</item>
/// <item>SSH：ssh-agent のサービスを起こし（初回だけ管理者の確認）、git がその agent を見るよう <c>core.sshCommand</c> を Windows の OpenSSH に向け
/// （ほかの ssh を指す設定が既にあれば上書きせず、そう言う）、<c>ssh-add</c> で鍵を預ける。Windows の ssh-agent は預かった鍵を
/// 再起動をまたいで持つので、次からは合言葉を聞かれない。</item>
/// </list>
/// </summary>
public sealed class GitCredentialRegistrar : IGitCredentialRegistrar
{
    private readonly ISshAgentService _agent;
    private readonly string _openSshFolder;
    private readonly IReadOnlyDictionary<string, string>? _environment;
    private readonly Lazy<string?> _httpsHelper;

    /// <param name="environment">起こすプロセス（git・ssh-add）に足す環境。テストが <c>GIT_CONFIG_GLOBAL</c> で自分の設定ファイルへ向ける。</param>
    /// <param name="httpsHelper">HTTPS の資格情報ヘルパーの名前。null なら入っている GCM を探す。</param>
    public GitCredentialRegistrar(ISshAgentService agent, string? openSshFolder = null,
        IReadOnlyDictionary<string, string>? environment = null, string? httpsHelper = null)
    {
        _agent = agent;
        _openSshFolder = openSshFolder
                         ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH");
        _environment = environment;
        _httpsHelper = new Lazy<string?>(() => httpsHelper ?? DetectCredentialManager());
    }

    private string SshAddPath => Path.Combine(_openSshFolder, "ssh-add.exe");

    /// <summary>git が使う ssh（<c>core.sshCommand</c> に書く値）。git の設定は <c>/</c> 区切りが無難。</summary>
    public string SshCommand => Path.Combine(_openSshFolder, "ssh.exe").Replace('\\', '/');

    public bool CanRemember(GitCredentialRemember remember) => remember switch
    {
        GitCredentialRemember.Https => _httpsHelper.Value is not null,
        GitCredentialRemember.SshKey key => File.Exists(SshAddPath) && File.Exists(key.KeyPath),
        _ => false,
    };

    public Task<GitCredentialRegistrationResult> RememberAsync(GitCredentialRemember remember, string? userName, string secret,
        IReadOnlyDictionary<string, string>? answeringEnvironment, CancellationToken cancellationToken = default)
        => remember switch
        {
            GitCredentialRemember.Https https => RememberHttpsAsync(https, userName, secret, cancellationToken),
            GitCredentialRemember.SshKey key => RememberSshKeyAsync(key, answeringEnvironment, cancellationToken),
            _ => Task.FromResult(new GitCredentialRegistrationResult(false, "覚えさせ方が分かりませんでした。")),
        };

    private async Task<GitCredentialRegistrationResult> RememberHttpsAsync(GitCredentialRemember.Https https, string? userName,
        string password, CancellationToken cancellationToken)
    {
        if (_httpsHelper.Value is not { } helper)
            return new(false, "Git Credential Manager が見つからないので、覚えさせませんでした。");
        if (string.IsNullOrEmpty(userName))
            return new(false, $"{https.Origin} のユーザー名が分からないので、覚えさせませんでした。");
        var uri = new Uri(https.Origin);

        // その接続先に効くヘルパーが既にあれば足さない（全体に GCM が設定済みなど）。
        var existing = await RunAsync("git", ["config", "--global", "--get-urlmatch", "credential.helper", https.Origin],
            null, null, cancellationToken).ConfigureAwait(false);
        var added = false;
        if (existing.ExitCode != 0 || existing.Output.Trim().Length == 0)
        {
            var add = await RunAsync("git", ["config", "--global", "--add", $"credential.{https.Origin}.helper", helper],
                null, null, cancellationToken).ConfigureAwait(false);
            if (add.ExitCode != 0)
                return new(false, $"git の設定に資格情報ヘルパーを足せませんでした：{add.Error.Trim()}");
            added = true;
        }

        var input = new StringBuilder()
            .Append("protocol=").Append(uri.Scheme).Append('\n')
            .Append("host=").Append(uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}").Append('\n')
            .Append("username=").Append(userName).Append('\n')
            .Append("password=").Append(password).Append('\n')
            .Append('\n')
            .ToString();
        var approve = await RunAsync("git", ["credential", "approve"], input, null, cancellationToken).ConfigureAwait(false);
        if (approve.ExitCode != 0)
            return new(false, $"Git Credential Manager に保存できませんでした：{approve.Error.Trim()}");
        return new(true, $"{https.Origin} の資格情報を Git Credential Manager に覚えさせました。次からは聞かれません。"
                         + (added ? $"（この接続先だけ git の設定 credential.{https.Origin}.helper に {helper} を足しました）" : ""));
    }

    private async Task<GitCredentialRegistrationResult> RememberSshKeyAsync(GitCredentialRemember.SshKey key,
        IReadOnlyDictionary<string, string>? answeringEnvironment, CancellationToken cancellationToken)
    {
        if (!File.Exists(SshAddPath))
            return new(false, "Windows の OpenSSH クライアントが見つからないので、鍵を預けませんでした（設定 → システム → オプション機能 で追加できます）。");

        switch (await _agent.IsRunningAsync().ConfigureAwait(false))
        {
            case null:
                return new(false, "ssh-agent のサービスがありません（Windows の OpenSSH クライアントを入れ直してください）。");
            case false:
                var started = await _agent.StartAsync().ConfigureAwait(false);
                if (!started.Success) return started;
                break;
        }

        var current = await RunAsync("git", ["config", "--global", "--get", "core.sshCommand"], null, null, cancellationToken)
            .ConfigureAwait(false);
        var plan = SshCommandPlanner.Decide(current.ExitCode == 0 ? current.Output.Trim() : null,
            Environment.GetEnvironmentVariable("GIT_SSH_COMMAND") ?? Environment.GetEnvironmentVariable("GIT_SSH"), SshCommand);
        if (plan == SshCommandPlan.Set)
        {
            var set = await RunAsync("git", ["config", "--global", "core.sshCommand", SshCommand], null, null, cancellationToken)
                .ConfigureAwait(false);
            if (set.ExitCode != 0)
                return new(false, $"git の設定（core.sshCommand）を書けませんでした：{set.Error.Trim()}");
        }

        var add = await RunAsync(SshAddPath, [key.KeyPath], null, answeringEnvironment, cancellationToken).ConfigureAwait(false);
        if (add.ExitCode != 0)
            return new(false, $"鍵を ssh-agent に預けられませんでした：{(add.Error + add.Output).Trim()}");

        var name = Path.GetFileName(key.KeyPath);
        return plan switch
        {
            SshCommandPlan.Set => new(true, $"鍵 {name} を ssh-agent に預けました。次から合言葉は聞かれません。" +
                                            $"（git がこの ssh-agent を使うよう、git の設定 core.sshCommand を {SshCommand} にしました）"),
            SshCommandPlan.Conflict => new(true, $"鍵 {name} を ssh-agent に預けました。ただし git は別の ssh を使う設定"
                                                 + "（core.sshCommand か GIT_SSH）になっているので、次も聞かれるかもしれません。"),
            _ => new(true, $"鍵 {name} を ssh-agent に預けました。次から合言葉は聞かれません。"),
        };
    }

    /// <summary>GCM の名前。新しい Git for Windows は <c>manager</c>、古いものは <c>manager-core</c>。</summary>
    private string? DetectCredentialManager()
    {
        foreach (var (command, helper) in new[] { ("credential-manager", "manager"), ("credential-manager-core", "manager-core") })
        {
            var result = RunAsync("git", [command, "--version"], null, null, CancellationToken.None).GetAwaiter().GetResult();
            if (result.ExitCode == 0) return helper;
        }
        return null;
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(string exe, IReadOnlyList<string> args, string? input,
        IReadOnlyDictionary<string, string>? extra, CancellationToken cancellationToken)
    {
        // リポジトリの外で走らせる（作業中のリポジトリの設定が、全体の設定と GCM への保存に混ざらないように）。
        var outside = Path.Combine(Path.GetTempPath(), "loomo-credential");
        Directory.CreateDirectory(outside);
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = outside,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GCM_INTERACTIVE"] = "never";
        info.Environment["GIT_CEILING_DIRECTORIES"] = Path.GetDirectoryName(outside.TrimEnd('\\')) ?? outside;
        foreach (var source in new[] { _environment, extra })
            if (source is not null)
                foreach (var (k, v) in source) info.Environment[k] = v;
        try
        {
            using var process = Process.Start(info)!;
            var stdout = ChildProcessIo.ReadToEndAsync(process.StandardOutput, "credential:stdout");
            var stderr = ChildProcessIo.ReadToEndAsync(process.StandardError, "credential:stderr");
            if (input is not null)
            {
                // 標準入力の書き込みは UTF-8・BOM なし（git は先頭の BOM を鍵名の一部として読む）。
                var bytes = new UTF8Encoding(false).GetBytes(input);
                await process.StandardInput.BaseStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            process.StandardInput.Close();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 既に終了 */ }
                return (-1, "", $"{Path.GetFileName(exe)} が応答しません。");
            }
            return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (Win32Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }
}

/// <summary>git が使う ssh の設定をどうするか（純関数）。</summary>
public enum SshCommandPlan
{
    /// <summary>何も設定されていない——Windows の OpenSSH へ向ける。</summary>
    Set,
    /// <summary>既に Windows の OpenSSH を向いている。</summary>
    Keep,
    /// <summary>ほかの ssh を指す設定がある——人の設定は上書きしない。</summary>
    Conflict,
}

public static class SshCommandPlanner
{
    /// <param name="configured">git 全体の設定の <c>core.sshCommand</c>（無ければ null）。</param>
    /// <param name="environment">Loomo の環境の <c>GIT_SSH_COMMAND</c>／<c>GIT_SSH</c>（無ければ null）。設定より強いので、あれば上書きしても効かない。</param>
    /// <param name="openSsh">Windows の OpenSSH の ssh（<c>/</c> 区切り）。</param>
    public static SshCommandPlan Decide(string? configured, string? environment, string openSsh)
    {
        if (!string.IsNullOrWhiteSpace(environment))
            return Same(environment, openSsh) ? SshCommandPlan.Keep : SshCommandPlan.Conflict;
        if (string.IsNullOrWhiteSpace(configured))
            return SshCommandPlan.Set;
        return Same(configured, openSsh) ? SshCommandPlan.Keep : SshCommandPlan.Conflict;
    }

    private static bool Same(string command, string openSsh)
        => string.Equals(command.Trim().Trim('"').Replace('\\', '/'), openSsh.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
