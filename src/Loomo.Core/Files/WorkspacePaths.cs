using System;
using System.Collections.Generic;
using System.IO;

namespace sk0ya.Loomo.Core.Files;

/// <summary>
/// ワークスペース（＝**フォルダーの集合**）に対するパスの問い。実装をここ1つに集める。
///
/// <para><b>なぜ共通化したか。</b>この判定は各機能が個別に書いていて、少なくとも8箇所に
/// 同じ形のコード（<c>GetFullPath</c> → 末尾区切り除去 → <c>StartsWith</c> + <c>OrdinalIgnoreCase</c>）が
/// あった。間違え方は2通りで、両方とも実際に起きている:</para>
/// <list type="number">
/// <item><b>プライマリだけを見る</b> — あとから追加したフォルダーのファイルが「ワークスペース外」になり、
///   rename やリファクタリングが丸ごと失敗する。</item>
/// <item><b>区切り文字を付けずに前方一致</b> — <c>C:\work\app2</c> を <c>C:\work\app</c> 配下と誤認する。</item>
/// </list>
/// <para>「気をつける」では防げないので、問いの方をここへ集めて呼ぶだけにする。
/// 消費者は基本的に <c>IWorkspaceService</c> 経由で使い、サービスを持てない場所だけ直接呼ぶ。</para>
/// </summary>
public static class WorkspacePaths
{
    /// <summary><paramref name="path"/> が <paramref name="folder"/> 自身かその配下か。</summary>
    public static bool IsWithin(string? folder, string? path)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(path)) return false;
        if (!TryFull(folder, out var root) || !TryFull(path, out var full)) return false;

        return full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>いずれかのフォルダー配下か。フォルダーが空なら常に false
    /// （「ワークスペースが開かれていない」＝何も許可しない）。</summary>
    public static bool Contains(IReadOnlyList<string>? folders, string? path)
        => FolderFor(folders, path) is not null;

    /// <summary>そのパスを担当するフォルダー。属さなければ null。
    /// 入れ子があった場合は**より深い（具体的な）方**を返す。</summary>
    public static string? FolderFor(IReadOnlyList<string>? folders, string? path)
    {
        if (folders is null || folders.Count == 0) return null;
        string? best = null;
        int bestLength = -1;
        foreach (var folder in folders)
        {
            if (!IsWithin(folder, path)) continue;
            if (!TryFull(folder, out var full)) continue;
            if (full.Length <= bestLength) continue;
            best = folder;
            bestLength = full.Length;
        }
        return best;
    }

    /// <summary>
    /// 一覧に出すための表記。フォルダーが2つ以上あるときだけ<b>フォルダー名を前置</b>し、
    /// 区切りは <c>/</c> に揃える（検索結果ツリーと Problems が使っている既存の表記を正本化したもの）。
    /// どのフォルダーにも属さないパスは絶対パスのまま返す——短く見せて出所を隠さない。
    /// </summary>
    public static string ToDisplayPath(IReadOnlyList<string>? folders, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        if (FolderFor(folders, path) is not { } folder) return path;

        string relative;
        try { relative = Path.GetRelativePath(folder, path); }
        catch { return path; }
        if (relative == ".") relative = "";

        if (folders!.Count > 1)
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
            relative = relative.Length == 0 ? name : $"{name}{Path.DirectorySeparatorChar}{relative}";
        }
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>パスの<b>等値比較用</b>の正規化（絶対化＋末尾区切りの除去）。解決できないパスは
    /// 与えられたまま返す——短くしようとして出所を変えない。
    /// <para>比較そのもの（大小無視）は呼び出し側で行う。ここを各所で書き直すと
    /// 「片方だけ <c>GetFullPath</c> していて一致しない」が起きるので、正規化はこの1本に寄せる。</para></summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        return TryFull(path, out var full) ? full : path;
    }

    private static bool TryFull(string value, out string full)
    {
        try
        {
            full = Path.GetFullPath(value)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.Length > 0;
        }
        catch
        {
            full = "";
            return false;
        }
    }
}
