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

    /// <summary>
    /// 遠隔との認証で止まった（§24.101）。資格情報が無い・違う・入力を取り消した・ホスト鍵を受け入れなかった、のどれか。
    /// git の文面は「Authentication failed」「could not read Username」、ssh の文面は「Permission denied (publickey」
    /// 「Host key verification failed」。ssh の文面は訳されない。git の訳は版で揺れるので英語だけを見る
    /// （Loomo の git は既定の言語で走るが、この 2 つは訳されていない版が多い。読み分けられなければ今まで通り git の文面）。
    /// </summary>
    public static bool IsAuthenticationFailure(string? error)
    {
        if (string.IsNullOrEmpty(error)) return false;
        return error.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
               || error.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
               || error.Contains("could not read Password", StringComparison.OrdinalIgnoreCase)
               || error.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase)
               || error.Contains("Permission denied (publickey", StringComparison.OrdinalIgnoreCase)
               || error.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>認証で止まったときに、git の文面の前に置く一文。
    /// 「もう一度やれば入力欄が出る」とは言わない——鍵が登録されていない（publickey）など、聞いても直らない止まり方も含むので。</summary>
    public const string AuthenticationFailureMessage =
        "認証で止まりました：資格情報が無いか、違っています（入力欄で取り消したときもこうなります）。" +
        "Git Credential Manager・ssh-agent・SSH 鍵の登録を確かめるか、ターミナルで同じ git の操作をして問いにそこで答えてください。";

    /// <summary>切り替える前に退避する stash のメッセージ（スタッシュの一覧でそれと分かるように）。</summary>
    public static string CheckoutStashMessage(string branch) => $"Loomo: {branch} へ切り替える前の変更";
}
