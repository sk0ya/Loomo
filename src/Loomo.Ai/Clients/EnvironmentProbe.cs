using System;
using System.Diagnostics;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// 実行環境で使える外部コマンドを判定する。検索ガイダンスを rg の有無で動的に切り替えるために使う。
/// 判定はワークスペースルート単位でキャッシュし、ルートが変わった（＝ワークスペースが決定し直された）
/// ときだけ実プロセスで再確認する。毎ターン実プロセスを起動しないための最適化。
/// </summary>
internal static class EnvironmentProbe
{
    private static readonly object Gate = new();
    private static bool? _hasRipgrep;
    private static string? _probedForRoot;

    /// <summary>ripgrep（<c>rg</c>）が PATH 上にあるか。ルートが前回判定時と変われば再判定する。</summary>
    public static bool HasRipgrep(string? workspaceRoot)
    {
        lock (Gate)
        {
            if (_hasRipgrep is { } cached && _probedForRoot == workspaceRoot)
                return cached;
            _probedForRoot = workspaceRoot;
            _hasRipgrep = Probe("rg", "--version");
            return _hasRipgrep.Value;
        }
    }

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
