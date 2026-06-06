using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Ai;

/// <summary>
/// Hugging Face から phi4-mini の ONNX（CPU int4）モデル一式をローカルへダウンロードする。
/// 取得先は <c>%APPDATA%/Loomo/models/&lt;name&gt;/</c>。各ファイルをストリーム保存し、
/// 既に正しいサイズで存在するファイルはスキップする（中断後の再実行で続きから取得できる）。
/// </summary>
public sealed class ModelDownloadService
{
    /// <summary>取得元リポジトリ。</summary>
    public const string Repo = "microsoft/Phi-4-mini-instruct-onnx";

    /// <summary>CPU int4 バリアントのフォルダ（リポジトリ内パス）。</summary>
    private const string Variant = "cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4";

    /// <summary>ローカル保存フォルダ名（モデルルート配下）。</summary>
    public const string ModelFolderName = "phi-4-mini-instruct-cpu-int4";

    /// <summary>バリアント配下の取得対象ファイル（保存時はこの相対名でフラットに置く）。</summary>
    private static readonly string[] Files =
    {
        "added_tokens.json",
        "config.json",
        "genai_config.json",
        "merges.txt",
        "model.onnx",
        "model.onnx.data",
        "special_tokens_map.json",
        "tokenizer.json",
        "tokenizer_config.json",
        "vocab.json",
    };

    private readonly HttpClient _http;

    public ModelDownloadService(HttpClient http) => _http = http;

    /// <summary>モデルルート（<c>%APPDATA%/Loomo/models</c>）。設定のモデル一覧もここを走査する。</summary>
    public static string DefaultModelsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "models");

    /// <summary>このサービスがダウンロードするモデルの保存先フォルダ。</summary>
    public string TargetDir => Path.Combine(DefaultModelsRoot, ModelFolderName);

    /// <summary>ダウンロード進捗。</summary>
    public sealed record Progress(long DownloadedBytes, long TotalBytes, string CurrentFile, int FileIndex, int FileCount);

    /// <summary>
    /// モデル一式をダウンロードし、保存先フォルダの絶対パスを返す。進捗は <paramref name="progress"/> に通知。
    /// 途中の失敗・キャンセル時は書きかけのファイルを片付けてから例外/キャンセルを伝播する。
    /// </summary>
    public async Task<string> DownloadAsync(IProgress<Progress>? progress, CancellationToken ct)
    {
        var target = TargetDir;
        Directory.CreateDirectory(target);

        // 進捗の総バイト数は HEAD で各ファイルサイズを合算して見積もる（取得不能なら 0 のまま）。
        var sizes = new long[Files.Length];
        long total = 0;
        for (var i = 0; i < Files.Length; i++)
        {
            sizes[i] = await GetContentLengthAsync(Files[i], ct);
            if (sizes[i] > 0) total += sizes[i];
        }

        long done = 0;
        for (var i = 0; i < Files.Length; i++)
        {
            var file = Files[i];
            var dest = Path.Combine(target, file);

            // 既に正しいサイズで存在すればスキップ（再実行時のレジューム）。
            if (sizes[i] > 0 && File.Exists(dest) && new FileInfo(dest).Length == sizes[i])
            {
                done += sizes[i];
                progress?.Report(new Progress(done, total, file, i + 1, Files.Length));
                continue;
            }

            progress?.Report(new Progress(done, total, file, i + 1, Files.Length));
            done += await DownloadFileAsync(file, dest, done, total, i, progress, ct);
        }

        return target;
    }

    private async Task<long> DownloadFileAsync(
        string file, string dest, long baseDone, long total, int index,
        IProgress<Progress>? progress, CancellationToken ct)
    {
        var tmp = dest + ".part";
        try
        {
            using var resp = await _http.GetAsync(Url(file), HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[1 << 20];   // 1MB
                long fileDone = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    fileDone += read;
                    progress?.Report(new Progress(baseDone + fileDone, total, file, index + 1, Files.Length));
                }
            }

            File.Move(tmp, dest, overwrite: true);
            return new FileInfo(dest).Length;
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    private async Task<long> GetContentLengthAsync(string file, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, Url(file));
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.IsSuccessStatusCode && resp.Content.Headers.ContentLength is { } len)
                return len;
        }
        catch (OperationCanceledException) { throw; }
        catch { /* サイズ不明（総量見積もりに含めないだけ） */ }
        return 0;
    }

    private static string Url(string file) =>
        $"https://huggingface.co/{Repo}/resolve/main/{Variant}/{file}";

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 片付け失敗は無視 */ }
    }
}
