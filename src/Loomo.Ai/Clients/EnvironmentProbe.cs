using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// 実行環境で使える外部コマンドを判定する。検索ガイダンスを rg の有無で動的に切り替えるために使う。
/// 判定はワークスペースルート単位でキャッシュし、各ルートにつき一度だけ実プロセスで確認する
/// （毎ターン実プロセスを起動しないための最適化）。ルートごとに独立したキャッシュ項目を持つので、
/// 複数ルートが交互に問い合わされても互いの判定を上書き（＝再プロービング）しない。
/// </summary>
internal static class EnvironmentProbe
{
    // ルート（null も含む）→ rg の有無。ルート単位で 1 回だけ確定させ、以降は固定値を返す。
    private static readonly ConcurrentDictionary<string, bool> Cache = new();

    /// <summary>ripgrep（<c>rg</c>）が PATH 上にあるか。ルートごとに一度だけ実判定し、結果を固定する。</summary>
    public static bool HasRipgrep(string? workspaceRoot)
        => Cache.GetOrAdd(workspaceRoot ?? "", static _ => Probe("rg", "--version"));

    private static bool Probe(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return false;
            if (!p.WaitForExit(2000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 既に終了 */ }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false; // 未インストール（Win32Exception）等
        }
    }
}
