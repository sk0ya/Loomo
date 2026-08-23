using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.Services;

/// <summary>
/// <c>git diff --name-status</c>／<c>git diff-tree --name-status</c> の出力をパースする。
/// 1行＝<c>状態&lt;TAB&gt;パス</c>、リネーム／コピーだけ <c>R100&lt;TAB&gt;旧パス&lt;TAB&gt;新パス</c> の3列になる。
/// 区切りが TAB なので<b>空白を含むパスもそのまま</b>読める。状態は先頭1文字だけを見る
/// （<c>R100</c> の類似度スコアは表示に使わない）。
///
/// <para><b>C クオートは解かない</b>。<see cref="GitCommandRunner"/> が渡す
/// <c>core.quotepath=false</c> が抑えるのは<b>非 ASCII だけ</b>（日本語のパスはそのまま出る）で、
/// 二重引用符・バックスラッシュ・TAB・改行を含むパスは今でも引用符付きの C クオート表記で出る。
/// その場合このパーサは引用符ごとの文字列を返す（実ファイルとして解決できず一覧に出るだけで、
/// 誤ったファイルを触ることはない）。実害が出たら unquote をここに足す。</para>
/// </summary>
public static class GitNameStatusParser
{
    public static IReadOnlyList<GitCommitFileChange> Parse(string output)
    {
        var changes = new List<GitCommitFileChange>();
        if (string.IsNullOrEmpty(output))
            return changes;

        foreach (var line in output.Split('\n'))
        {
            var value = line.TrimEnd('\r');
            if (value.Length == 0) continue;
            var parts = value.Split('\t');
            if (parts.Length < 2 || parts[0].Length == 0) continue;
            // 3列目があるのはリネーム／コピー。2列目が旧パス、3列目が新パス。
            var (path, originalPath) = parts.Length >= 3 && parts[2].Length > 0
                ? (parts[2], parts[1])
                : (parts[1], (string?)null);
            if (path.Length == 0) continue;
            changes.Add(new GitCommitFileChange(parts[0][0], path, originalPath));
        }
        return changes;
    }
}
