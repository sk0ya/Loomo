using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Ai;

/// <summary>
/// ローカルの Ollama サーバーが起動していなければ <c>ollama serve</c> の起動を試みる補助。
/// ローカルLLM（既定 <c>http://localhost:11434/v1</c>）利用時、サーバーの手動起動を不要にする。
/// 遠隔エンドポイントやインストールされていない場合は何もしない（呼び出し側で接続エラーになる）。
/// </summary>
public static class OllamaLauncher
{
    /// <summary>BaseUrl がローカルループバックを指すか（自動起動の対象判定）。</summary>
    public static bool IsLoopback(string? baseUrl) =>
        !string.IsNullOrWhiteSpace(baseUrl) &&
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
        (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// サーバーが応答するか確認し、応答せず かつ ループバック宛てなら <c>ollama serve</c> を起動して
    /// 応答するまで（最大 <paramref name="timeout"/>）待つ。起動・確認できれば true。
    /// </summary>
    public static async Task<bool> EnsureRunningAsync(
        HttpClient http, string baseUrl, CancellationToken ct, TimeSpan? timeout = null)
    {
        var root = baseUrl.TrimEnd('/');
        if (await IsUpAsync(http, root, ct)) return true;
        if (!IsLoopback(baseUrl)) return false; // 遠隔エンドポイントは起動できない
        if (!TryStartServe()) return false;     // ollama 未インストール等

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, ct);
            if (await IsUpAsync(http, root, ct)) return true;
        }
        return false;
    }

    /// <summary>エンドポイントに（4xx含め）HTTP応答があればサーバーは起動中とみなす。</summary>
    private static async Task<bool> IsUpAsync(HttpClient http, string root, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            using var resp = await http.GetAsync($"{root}/models", cts.Token);
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false; // 接続拒否・タイムアウト＝未起動
        }
    }

    private static bool TryStartServe()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("ollama", "serve")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p is null) return false;
            // 出力を読み捨ててバッファ詰まりによる子プロセスのブロックを防ぐ（fire-and-forget）。
            p.OutputDataReceived += static (_, _) => { };
            p.ErrorDataReceived += static (_, _) => { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
