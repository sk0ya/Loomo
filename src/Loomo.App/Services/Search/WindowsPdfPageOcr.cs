using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using sk0ya.Loomo.Services.Search;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// スキャンした PDF のページを Windows 標準の部品だけで読み取る（追加のモデル・ライブラリ不要）：
/// <c>Windows.Data.Pdf</c> でページを画像にし、<c>Windows.Media.Ocr</c> で文字にする。
/// <para>
/// 言語は日本語を優先し、無ければユーザーの言語設定から選ぶ（日本語のエンジンは英数字も読む）。
/// どれも入っていなければ <see cref="IsAvailable"/> が false になり、スキャンのページは今までどおり当たらないだけ。
/// </para>
/// <para>
/// 文書の本文検索（<see cref="DocumentTextCache"/>）から、<b>本文を持たないページにだけ</b>呼ばれる。検索の取り出しは
/// 数本並列に走るので、エンジンは呼び出しごとに作る（作るのは軽く、同じ実体を並列に使ってよいとは書かれていない）。
/// </para>
/// </summary>
public sealed class WindowsPdfPageOcr : IPdfPageOcr
{
    // 画像の幅。A4 縦で約 190dpi——本文の文字が読めて、OCR の上限（MaxImageDimension）にも収まる。
    private const uint RenderWidth = 1600;

    private readonly Lazy<bool> _available = new(() => CreateEngine() is not null);

    public bool IsAvailable => _available.Value;

    public string Recognize(string pdfPath, int pageNumber, CancellationToken ct)
        => RecognizeAsync(pdfPath, pageNumber, ct).GetAwaiter().GetResult();

    private static async Task<string> RecognizeAsync(string pdfPath, int pageNumber, CancellationToken ct)
    {
        if (CreateEngine() is not { } engine)
            return "";
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath)).AsTask(ct);
        var document = await PdfDocument.LoadFromFileAsync(file).AsTask(ct);
        if (pageNumber < 1 || pageNumber > document.PageCount)
            return "";

        using var page = document.GetPage((uint)(pageNumber - 1));
        using var stream = new InMemoryRandomAccessStream();
        var width = Math.Min(RenderWidth, OcrEngine.MaxImageDimension);
        var height = page.Size.Width > 0 ? page.Size.Height * width / page.Size.Width : width;
        if (height > OcrEngine.MaxImageDimension)   // 極端に縦長のページは高さで抑える
            width = (uint)(width * OcrEngine.MaxImageDimension / height);
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = width }).AsTask(ct);

        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
            .AsTask(ct);
        var result = await engine.RecognizeAsync(bitmap).AsTask(ct);
        return string.Join("\n", result.Lines.Select(line => line.Text));
    }

    private static OcrEngine? CreateEngine()
    {
        try
        {
            var japanese = new Windows.Globalization.Language("ja");
            return OcrEngine.IsLanguageSupported(japanese)
                ? OcrEngine.TryCreateFromLanguage(japanese)
                : OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch
        {
            return null;   // OCR の部品が無い環境（Server Core 等）
        }
    }
}
