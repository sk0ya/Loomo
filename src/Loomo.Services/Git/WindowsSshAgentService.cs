using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Processes;

namespace sk0ya.Loomo.Services;

/// <summary>
/// Windows の ssh-agent サービス（OpenSSH Authentication Agent）。既定は「無効」で止まっているので、鍵を預ける前に
/// 自動起動にして起こす（§24.101）。サービスの設定は管理者にしか変えられないので、その一手だけ昇格して走らせる——
/// 管理者の確認（UAC）は人が「次から聞かれないようにする」を選んだ後、初めての 1 回だけ出る。
/// </summary>
public sealed class WindowsSshAgentService : ISshAgentService
{
    private const string ServiceName = "ssh-agent";

    public async Task<bool?> IsRunningAsync()
    {
        var info = new ProcessStartInfo("sc.exe", $"query {ServiceName}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        try
        {
            using var process = Process.Start(info)!;
            var output = await ChildProcessIo.ReadToEndAsync(process.StandardOutput, "sc:stdout").ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            // 1060 = サービスが無い（OpenSSH クライアントが入っていない）。表示は訳されるので状態の語は英語の識別子で見る。
            if (process.ExitCode == 1060) return null;
            return output.Contains("RUNNING", StringComparison.Ordinal);
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    public async Task<GitCredentialRegistrationResult> StartAsync()
    {
        var info = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"Set-Service {ServiceName} -StartupType Automatic; Start-Service {ServiceName}\"")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var process = Process.Start(info)!;
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new(false, "管理者の確認が取り消されたので、ssh-agent を起こさず、鍵も預けませんでした。");
        }
        catch (Win32Exception ex)
        {
            return new(false, $"ssh-agent を起こせませんでした：{ex.Message}");
        }
        return await IsRunningAsync().ConfigureAwait(false) == true
            ? new(true, "ssh-agent を自動起動にして起こしました。")
            : new(false, "ssh-agent を起こせませんでした（サービスの状態を確かめてください）。");
    }
}
