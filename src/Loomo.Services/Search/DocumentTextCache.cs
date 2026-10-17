using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace sk0ya.Loomo.Services.Search;

/// <summary>
/// 文書（PDF・Word・Excel・PowerPoint）から取り出した1行。
/// </summary>
/// <param name="Position">開いたときに移る先の番号（PDF＝ページ、Excel＝シート、PowerPoint＝スライド、Word＝段落。1始まり）。</param>
/// <param name="Location">一覧に出す位置の表記（"p.3"・"Sheet1!B3"・"スライド 4"・"¶12"）。</param>
/// <param name="Text">本文の1行（改行を含まない）。</param>
public sealed record DocumentLine(int Position, string Location, string Text);

/// <summary>PDF の1ページを画像として読み取る（OCR）。本文を持たないページ（スキャン）にだけ使う。</summary>
public interface IPdfPageOcr
{
    /// <summary>この環境で読み取れるか（OCR エンジン・言語が無ければ false）。</summary>
    bool IsAvailable { get; }

    /// <summary><paramref name="pageNumber"/>（1始まり）を読み取った本文。読めなければ空。</summary>
    string Recognize(string pdfPath, int pageNumber, CancellationToken ct);
}

/// <summary>
/// 文書から取り出したテキストを、検索のたびに取り出し直さないよう一時的に覚えておく。
/// <para>
/// 置き場は <c>%LOCALAPPDATA%\Loomo\cache\document-text\</c>。鍵は「フルパス＋サイズ＋更新日時」なので、
/// 文書を書き換えれば別の鍵になって取り出し直しになる（古い方は掃除まで残るだけで、読まれない）。
/// 複数の Loomo で共有し、<b>最後の Loomo が終わるときに丸ごと消す</b>（<see cref="CleanupIfLastInstance"/>）。
/// 落ちて消し損ねたぶんは、次に「ほかに Loomo がいない」状態で起動したときに消す。
/// </para>
/// <para>
/// 取り出せなかった文書（壊れている・暗号化・画像だけ）も「本文なし」として覚える——毎回の検索で同じ
/// 失敗をやり直さないため。OCR を使えるかどうかも鍵に混ぜる（使えるようになったら読み直す）。
/// </para>
/// </summary>
public sealed class DocumentTextCache
{
    /// <summary>これより大きい文書は読まない（取り出しに時間とメモリを食いすぎるため）。</summary>
    public const long MaxDocumentBytes = 64L * 1024 * 1024;

    // 保存形式を変えたら上げる（鍵に混ぜるので、古い形式のファイルは読まれなくなる）。
    private const string FormatVersion = "v2";

    private readonly IPdfPageOcr? _ocr;

    public DocumentTextCache(string directory, IPdfPageOcr? ocr = null)
    {
        CacheDirectory = directory;
        _ocr = ocr;
    }

    public static string DefaultDirectory => Path.Combine(CacheRoot, "document-text");

    private static string CacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Loomo", "cache");

    public string CacheDirectory { get; }

    /// <summary>本文を検索できる文書か（拡張子で判定）。</summary>
    public static bool IsDocument(string path)
        => DocumentKinds.Contains(Path.GetExtension(path));

    /// <summary>本文を検索できる文書の拡張子。</summary>
    public static IReadOnlyCollection<string> DocumentExtensions => DocumentKinds;

    private static readonly HashSet<string> DocumentKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".xlsx", ".xlsm", ".pptx",
    };

    /// <summary>
    /// <paramref name="path"/> の本文の行。覚えていればそれを返し、無ければ取り出して覚える。
    /// 読めない・大きすぎる文書は空。キャンセルされたときは覚えない。
    /// </summary>
    public IReadOnlyList<DocumentLine> GetLines(string path, CancellationToken ct)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxDocumentBytes || !IsDocument(path))
                return Array.Empty<DocumentLine>();
        }
        catch { return Array.Empty<DocumentLine>(); }

        var ocr = _ocr is { IsAvailable: true } && IsPdf(path) ? _ocr : null;
        var cacheFile = Path.Combine(CacheDirectory, KeyFor(info, ocr is not null) + ".txt");
        if (TryRead(cacheFile) is { } cached)
            return cached;

        var lines = Extract(info.FullName, ocr, ct);
        ct.ThrowIfCancellationRequested();
        TryWrite(cacheFile, lines);
        return lines;
    }

    private static bool IsPdf(string path) => path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<DocumentLine> Extract(string path, IPdfPageOcr? ocr, CancellationToken ct)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".pdf" => PdfTextExtractor.ExtractLines(path, ocr, ct),
            ".docx" => OfficeTextExtractor.ExtractWord(path, ct),
            ".xlsx" or ".xlsm" => OfficeTextExtractor.ExtractExcel(path, ct),
            ".pptx" => OfficeTextExtractor.ExtractPowerPoint(path, ct),
            _ => Array.Empty<DocumentLine>(),
        };

    private static string KeyFor(FileInfo info, bool ocr)
    {
        var source = $"{FormatVersion}|{(ocr ? "ocr" : "text")}|{info.FullName.ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..32];
    }

    // 1行＝「番号<TAB>位置<TAB>本文」。本文の改行・タブは取り出し時に空白へ置き換え済み。
    private static IReadOnlyList<DocumentLine>? TryRead(string cacheFile)
    {
        try
        {
            if (!File.Exists(cacheFile))
                return null;
            var lines = new List<DocumentLine>();
            foreach (var raw in File.ReadLines(cacheFile, Encoding.UTF8))
            {
                var first = raw.IndexOf('\t');
                var second = first < 0 ? -1 : raw.IndexOf('\t', first + 1);
                if (second < 0 || !int.TryParse(raw.AsSpan(0, first), NumberStyles.None, CultureInfo.InvariantCulture, out var position))
                    return null;   // 壊れている：取り出し直す
                lines.Add(new DocumentLine(position, raw[(first + 1)..second], raw[(second + 1)..]));
            }
            return lines;
        }
        catch { return null; }   // 別の Loomo が書いている途中・掃除中：取り出し直せばよい
    }

    private void TryWrite(string cacheFile, IReadOnlyList<DocumentLine> lines)
    {
        try
        {
            // 掃除と行き違っても書けるよう、毎回作る（在れば何もしない）。
            Directory.CreateDirectory(CacheDirectory);
            // 書きかけを読まれないよう、一時ファイルに書いてから置き換える。
            var temp = cacheFile + "." + Environment.ProcessId + ".tmp";
            File.WriteAllLines(temp,
                lines.Select(l => string.Create(CultureInfo.InvariantCulture, $"{l.Position}\t{l.Location}\t{l.Text}")),
                Encoding.UTF8);
            File.Move(temp, cacheFile, overwrite: true);
        }
        catch { /* 覚えられないだけ。次の検索で取り出し直す */ }
    }

    /// <summary>行の本文を1行に収める（改行・タブを空白へ。キャッシュの区切りと衝突させない）。</summary>
    internal static string OneLine(string text)
        => text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();

    /// <summary>
    /// ほかに Loomo が動いていなければ、置き場を丸ごと消す。終了時（最後の1つなら消える）と
    /// 起動時（落ちて消し損ねたぶん）に呼ぶ。同時に2つ終わると互いを見て消さないことがあるが、
    /// そのぶんは次の起動で消える。
    /// </summary>
    public static void CleanupIfLastInstance()
    {
        if (OtherInstanceAlive())
            return;
        // pdf-text は PDF だけを覚えていた頃の置き場（形式が違うので読まれない）。
        foreach (var dir in new[] { DefaultDirectory, Path.Combine(CacheRoot, "pdf-text") })
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch { /* 握られているファイルがあれば残る。次の機会に消す */ }
        }
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
