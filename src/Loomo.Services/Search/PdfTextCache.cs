using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace sk0ya.Loomo.Services.Search;

/// <summary>
/// PDF から取り出したページごとのテキストを、検索のたびに取り出し直さないよう一時的に覚えておく。
/// <para>
/// 置き場は <c>%LOCALAPPDATA%\Loomo\cache\pdf-text\</c>。鍵は「フルパス＋サイズ＋更新日時」なので、
/// PDF を書き換えれば別の鍵になって取り出し直しになる（古い方は掃除まで残るだけで、読まれない）。
/// 複数の Loomo で共有し、<b>最後の Loomo が終わるときに丸ごと消す</b>（<see cref="CleanupIfLastInstance"/>）。
/// 落ちて消し損ねたぶんは、次に「ほかに Loomo がいない」状態で起動したときに消す。
/// </para>
/// <para>
/// 取り出せなかった PDF（壊れている・暗号化・画像だけ）も「本文なし」として覚える——毎回の検索で同じ
/// 失敗をやり直さないため。
/// </para>
/// </summary>
public sealed class PdfTextCache
{
    /// <summary>これより大きい PDF は読まない（取り出しに時間とメモリを食いすぎるため）。</summary>
    public const long MaxPdfBytes = 64L * 1024 * 1024;

    // ページの区切り。取り出した本文からは取り除いてあるので、本文に紛れ込むことは無い。
    private const char PageSeparator = '\f';
    // 保存形式を変えたら上げる（鍵に混ぜるので、古い形式のファイルは読まれなくなる）。
    private const string FormatVersion = "v1";

    public PdfTextCache(string directory) => CacheDirectory = directory;

    /// <summary>既定の置き場（<c>%LOCALAPPDATA%\Loomo\cache\pdf-text</c>）。</summary>
    public static PdfTextCache Default { get; } = new(DefaultDirectory);

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Loomo", "cache", "pdf-text");

    public string CacheDirectory { get; }

    /// <summary>
    /// <paramref name="pdfPath"/> のページごとのテキスト（1ページ目が [0]）。覚えていればそれを返し、
    /// 無ければ取り出して覚える。読めない・大きすぎる PDF は空。キャンセルされたときは覚えない。
    /// </summary>
    public IReadOnlyList<string> GetPages(string pdfPath, CancellationToken ct)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(pdfPath);
            if (!info.Exists || info.Length > MaxPdfBytes)
                return Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }

        var cacheFile = Path.Combine(CacheDirectory, KeyFor(info) + ".txt");
        if (TryRead(cacheFile) is { } cached)
            return cached;

        var pages = PdfTextExtractor.ExtractPages(info.FullName, ct);
        ct.ThrowIfCancellationRequested();
        TryWrite(cacheFile, pages);
        return pages;
    }

    private static string KeyFor(FileInfo info)
    {
        var source = $"{FormatVersion}|{info.FullName.ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..32];
    }

    private static IReadOnlyList<string>? TryRead(string cacheFile)
    {
        try
        {
            if (!File.Exists(cacheFile))
                return null;
            var text = File.ReadAllText(cacheFile, Encoding.UTF8);
            return text.Length == 0 ? Array.Empty<string>() : text.Split(PageSeparator);
        }
        catch { return null; }   // 別の Loomo が書いている途中・掃除中：取り出し直せばよい
    }

    private void TryWrite(string cacheFile, IReadOnlyList<string> pages)
    {
        try
        {
            // 掃除と行き違っても書けるよう、毎回作る（在れば何もしない）。
            Directory.CreateDirectory(CacheDirectory);
            // 書きかけを読まれないよう、一時ファイルに書いてから置き換える。
            var temp = cacheFile + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, string.Join(PageSeparator, pages), Encoding.UTF8);
            File.Move(temp, cacheFile, overwrite: true);
        }
        catch { /* 覚えられないだけ。次の検索で取り出し直す */ }
    }

    /// <summary>
    /// ほかに Loomo が動いていなければ、置き場を丸ごと消す。終了時（最後の1つなら消える）と
    /// 起動時（落ちて消し損ねたぶん）に呼ぶ。同時に2つ終わると互いを見て消さないことがあるが、
    /// そのぶんは次の起動で消える。
    /// </summary>
    public static void CleanupIfLastInstance()
    {
        if (OtherInstanceAlive())
            return;
        try
        {
            if (Directory.Exists(DefaultDirectory))
                Directory.Delete(DefaultDirectory, recursive: true);
        }
        catch { /* 握られているファイルがあれば残る。次の機会に消す */ }
    }

    private static bool OtherInstanceAlive()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            var others = Process.GetProcessesByName(self.ProcessName);
            try { return others.Any(p => p.Id != self.Id); }
            finally { foreach (var p in others) p.Dispose(); }
        }
        catch { return true; }   // 判定できないときは消さない側へ倒す
    }
}

/// <summary>PDF のページごとのテキストを取り出す（PdfPig）。</summary>
public static class PdfTextExtractor
{
    /// <summary>
    /// ページごとのテキスト。読めない PDF（壊れている・暗号化）は空。ページ単位の失敗はそのページを空にして続ける。
    /// </summary>
    public static IReadOnlyList<string> ExtractPages(string path, CancellationToken ct)
    {
        try
        {
            using var document = PdfDocument.Open(path, new ParsingOptions
            {
                UseLenientParsing = true,
                SkipMissingFonts = true,
            });
            var pages = new List<string>(document.NumberOfPages);
            for (var number = 1; number <= document.NumberOfPages; number++)
            {
                ct.ThrowIfCancellationRequested();
                string text;
                try { text = ContentOrderTextExtractor.GetText(document.GetPage(number)); }
                catch (OperationCanceledException) { throw; }
                catch { text = ""; }
                pages.Add(Normalize(text));
            }
            return pages;
        }
        catch (OperationCanceledException) { throw; }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// 検索しやすい形に整える。PDF は文字を座標で置いているだけなので、取り出すと和文の文字の間に
    /// 空白が挟まることがある（「検 索」）——<b>非 ASCII の文字どうしに挟まれた空白だけ</b>を詰める
    /// （英単語の間の空白は残す）。改行は \n にそろえ、ページ区切りに使う \f は取り除く。
    /// </summary>
    internal static string Normalize(string text)
    {
        if (text.Length == 0)
            return text;
        var source = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\f", "");
        var sb = new StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c == ' ' && sb.Length > 0 && sb[^1] > 0x7F)
            {
                var next = i + 1;
                while (next < source.Length && source[next] == ' ')
                    next++;
                if (next < source.Length && source[next] > 0x7F)
                {
                    i = next - 1;
                    continue;
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
