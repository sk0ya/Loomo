using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.Services;

/// <summary>
/// git が止めた操作の理由を読み分ける（§24.99）。止まった理由が分かれば、git の文面の代わりに「次の一手」を出せる。
/// 純関数。読み分けられない文面は null／false を返し、呼び出し側は今まで通り git の文面を出す（推測で言い換えない）。
/// </summary>
public static class GitRefusal
{
    /// <summary>リモートに手元に無いコミットがあって push が拒まれた（非早送り・<c>fetch first</c>）。
    /// <c>--force-with-lease</c> の拒否（<c>stale info</c>）はこれではない——そちらは取り込んでも直らない。</summary>
    public static bool IsPushBehind(string? error)
    {
        if (string.IsNullOrEmpty(error)) return false;
        if (error.Contains("stale info", StringComparison.OrdinalIgnoreCase)) return false;
        return error.Contains("[rejected]", StringComparison.Ordinal)
               && (error.Contains("fetch first", StringComparison.OrdinalIgnoreCase)
                   || error.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
                   || error.Contains("(フェッチしてください)", StringComparison.Ordinal)
                   || error.Contains("早送りでない", StringComparison.Ordinal));
    }

    /// <summary>push が拒まれたときに、git の文面の代わりに出す一文。</summary>
    public const string PushBehindMessage =
        "プッシュは拒まれました：リモートに、手元に無いコミットがあります。先に取り込んで（プル）から押し直してください。";

    /// <summary>チェックアウトがローカル変更とぶつかって止まったときの、ぶつかったファイル（git の一覧の順）。
    /// 止まった理由がそれでなければ null。<paramref name="untracked"/> は「追跡していないファイルが上書きされる」方。</summary>
    public static IReadOnlyList<string>? CheckoutBlockingFiles(string? error, out bool untracked)
    {
        untracked = false;
        if (string.IsNullOrEmpty(error)) return null;
        var lines = error.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, IsCheckoutOverwriteHeader);
        if (start < 0) return null;
        untracked = lines[start].Contains("untracked", StringComparison.OrdinalIgnoreCase)
                    || lines[start].Contains("追跡されていない", StringComparison.Ordinal);
        var files = new List<string>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            // git は一覧の各行をタブで字下げする。字下げの無い行（「Please commit…」「Aborting」）で一覧は終わる。
            if (line.Length == 0 || !char.IsWhiteSpace(line[0])) break;
            var path = line.Trim();
            if (path.Length > 0) files.Add(path);
        }
        return files;
    }

    private static bool IsCheckoutOverwriteHeader(string line)
        => (line.Contains("would be overwritten by checkout", StringComparison.OrdinalIgnoreCase)
            || line.Contains("would be overwritten by switch", StringComparison.OrdinalIgnoreCase)
            || (line.Contains("上書き", StringComparison.Ordinal)
                && (line.Contains("チェックアウト", StringComparison.Ordinal) || line.Contains("切り替え", StringComparison.Ordinal))));

    /// <summary>切り替える前に退避する stash のメッセージ（スタッシュの一覧でそれと分かるように）。</summary>
    public static string CheckoutStashMessage(string branch) => $"Loomo: {branch} へ切り替える前の変更";
}
