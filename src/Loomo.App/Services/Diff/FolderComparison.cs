using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Diff;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Services;

/// <summary>フォルダー同士の比較の結果。<paramref name="Comparisons"/> は違いのあるファイルだけ（相対パス順）。</summary>
/// <param name="Identical">中身まで同じだったファイルの数。</param>
/// <param name="Skipped">違うがテキストとして並べられないファイル（バイナリ・大きすぎる・読めない）の相対パス。</param>
/// <param name="Truncated">違いの数が上限に達して打ち切った（一覧は途中まで）。</param>
/// <param name="TooManyFiles">片側のファイルが上限を超えたので比べなかった。途中まで数えた側と比べると、
/// 数え残したファイルが「片側にしか無い」に化けるため、一部だけ見せることはしない。</param>
internal sealed record FolderComparisonResult(
    IReadOnlyList<DiffComparison> Comparisons, int Identical, IReadOnlyList<string> Skipped, bool Truncated,
    bool TooManyFiles = false);

/// <summary>
/// フォルダー同士を比べる（§24.5.3、Zed 1.24 の <c>zed --diff</c> のディレクトリ版に当たる）。Git の外にある2つの版
/// ——展開した配布物、バックアップ、別の PC から持ってきたコピー——を部屋の中で突き合わせる。
/// <para>結果は 1 ファイル 1 件の <see cref="DiffComparison"/> にして、Diff ペインの比較のストックへ積む。
/// 新しい面は作らない：違うファイルの一覧はそのまま比較の一覧で、選べば既存の左右差分で読める。</para>
/// <para>片側にしか無いファイルは、無い側を空にした比較（「追加」「削除」）。同じファイルは数だけ数えて並べない。
/// <c>.git</c> の中は比べない（オブジェクトの差は読める差分にならない）。</para>
/// </summary>
internal static class FolderComparison
{
    /// <summary>片側でこれより多くファイルがあれば打ち切る（巨大なツリーを丸ごと読んで止まらないように）。</summary>
    public const int MaxFilesPerSide = 20_000;

    /// <summary>並べる違いの上限。これを超えるなら比較の一覧として読める量ではない。</summary>
    public const int MaxDifferences = 200;

    /// <summary>テキストとして読み込む上限。超えるものは「並べられない違い」に回す。</summary>
    public const long MaxTextBytes = 1024 * 1024;

    public static FolderComparisonResult Build(
        string leftRoot, string rightRoot, CancellationToken ct = default, int maxFilesPerSide = MaxFilesPerSide)
    {
        var tooMany = false;
        var left = Enumerate(leftRoot, maxFilesPerSide, ref tooMany, ct);
        var right = tooMany ? [] : Enumerate(rightRoot, maxFilesPerSide, ref tooMany, ct);
        if (tooMany)
            return new FolderComparisonResult([], 0, [], Truncated: false, TooManyFiles: true);
        var truncated = false;
        var leftName = FolderName(leftRoot);
        var rightName = FolderName(rightRoot);
        if (string.Equals(leftName, rightName, StringComparison.OrdinalIgnoreCase))
            (leftName, rightName) = (leftRoot, rightRoot);   // 同名のフォルダー同士はパスで見分ける

        var all = left.Keys.Union(right.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        var comparisons = new List<DiffComparison>();
        var skipped = new List<string>();
        var identical = 0;
        foreach (var rel in all)
        {
            ct.ThrowIfCancellationRequested();
            if (comparisons.Count + skipped.Count >= MaxDifferences)
            {
                truncated = true;
                break;
            }
            var hasLeft = left.TryGetValue(rel, out var leftPath);
            var hasRight = right.TryGetValue(rel, out var rightPath);
            if (hasLeft && hasRight && SameBytes(leftPath!, rightPath!))
            {
                identical++;
                continue;
            }
            if ((hasLeft && !IsText(leftPath!)) || (hasRight && !IsText(rightPath!)))
            {
                skipped.Add(rel);
                continue;
            }
            try
            {
                var leftText = hasLeft ? File.ReadAllText(leftPath!) : "";
                var rightText = hasRight ? File.ReadAllText(rightPath!) : "";
                // 行数はここ（裏）で数えて素材に持たせる。差分は改行コード（CRLF／LF）を区別しないので、
                // バイト列は違っても差分の無いものは「同一」に数える——Windows の checkout と zip の展開を
                // 比べると全ファイルが「+0 −0」で並び、本当の違いが埋もれるため。
                var stat = DiffUtil.Stat(leftText, rightText);
                if (hasLeft && hasRight && stat == (0, 0))
                {
                    identical++;
                    continue;
                }
                comparisons.Add(new DiffComparison(
                    $"{leftName}/{rel}", leftText, $"{rightName}/{rel}", rightText,
                    FilePath: hasRight ? rightPath! : leftPath!, FileIsLeft: !hasRight)
                {
                    Label = rel,
                    Badge = !hasLeft ? "追加" : !hasRight ? "削除" : "",
                    Stat = stat,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(rel);
            }
        }
        return new FolderComparisonResult(comparisons, identical, skipped, truncated);
    }

    /// <summary>結果を一言で（トースト用）。</summary>
    public static string Summarize(FolderComparisonResult result)
    {
        if (result.TooManyFiles)
            return $"ファイルが多すぎるため比較しませんでした（片側 {MaxFilesPerSide:N0} 件まで）。もっと下のフォルダー同士を選んでください。";
        var parts = new List<string> { $"違い {result.Comparisons.Count} 件", $"同一 {result.Identical} 件" };
        if (result.Skipped.Count > 0)
            parts.Add($"テキストで並べられない違い {result.Skipped.Count} 件（{string.Join("、", result.Skipped.Take(3))}{(result.Skipped.Count > 3 ? " ほか" : "")}）");
        var text = string.Join("・", parts);
        return result.Truncated ? text + "。件数が多いため途中で打ち切りました" : text;
    }

    /// <summary>相対パス（区切りは <c>/</c>）→ 実パス。</summary>
    private static Dictionary<string, string> Enumerate(string root, int maxFiles, ref bool tooMany, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    if (!string.Equals(Path.GetFileName(sub), ".git", StringComparison.OrdinalIgnoreCase))
                        pending.Push(sub);
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (result.Count >= maxFiles)
                    {
                        tooMany = true;
                        return result;
                    }
                    result[Path.GetRelativePath(root, file).Replace('\\', '/')] = file;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 読めないフォルダーは飛ばす（片側だけ読めないと、その中身が全部「追加／削除」に見えるが、
                // 止めるよりは比べられる分を見せる）。
            }
        }
        return result;
    }

    private static bool IsText(string path)
    {
        try { return new FileInfo(path).Length <= MaxTextBytes && !BinaryFileDetector.IsBinary(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool SameBytes(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            using var sa = fa.OpenRead();
            using var sb = fb.OpenRead();
            Span<byte> ba = stackalloc byte[16 * 1024];
            Span<byte> bb = stackalloc byte[16 * 1024];
            while (true)
            {
                var na = sa.ReadAtLeast(ba, ba.Length, throwOnEndOfStream: false);
                var nb = sb.ReadAtLeast(bb, bb.Length, throwOnEndOfStream: false);
                if (na != nb || !ba[..na].SequenceEqual(bb[..nb])) return false;
                if (na == 0) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string FolderName(string path)
        => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name
            ? name : path;
}
