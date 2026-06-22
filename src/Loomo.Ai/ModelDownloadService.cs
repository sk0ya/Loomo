using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Ai;

/// <summary>
/// ダウンロード可能な ONNX（ORT-GenAI 互換）モデルの定義。Hugging Face のリポジトリと、
/// その中の取得対象ファイル群を持つ。<paramref name="Subfolder"/> はリポジトリ内のバリアントフォルダ
/// （ルート直置きなら空文字）。保存時はこの相対名でフラットに <c>%APPDATA%/Loomo/models/&lt;FolderName&gt;/</c> へ置く。
/// </summary>
/// <param name="Id">プロファイル解決にも使う安定 ID（フォルダ名と一致）。</param>
/// <param name="DisplayName">設定画面に出す表示名（サイズ目安込み）。</param>
/// <param name="Repo">Hugging Face リポジトリ（<c>owner/name</c>）。</param>
/// <param name="Subfolder">リポジトリ内のバリアントフォルダ（ルートなら ""）。</param>
/// <param name="FolderName">ローカル保存フォルダ名（モデルルート配下）。</param>
/// <param name="Files">取得対象ファイル（Subfolder 配下の相対名）。</param>
public sealed record DownloadableModel(
    string Id,
    string DisplayName,
    string Repo,
    string Subfolder,
    string FolderName,
    string[] Files)
{
    /// <summary>保存先フォルダの絶対パス。</summary>
    public string TargetDir => Path.Combine(ModelDownloadService.DefaultModelsRoot, FolderName);
}

/// <summary>
/// Hugging Face からモデル一式をローカルへダウンロードする。取得先は
/// <c>%APPDATA%/Loomo/models/&lt;FolderName&gt;/</c>。各ファイルをストリーム保存し、既に正しいサイズで
/// 存在するファイルはスキップする（中断後の再実行で続きから取得できる）。取得対象は <see cref="Catalog"/>
/// （現在は GGUF・llama.cpp 用：Qwen3-4B Q4_K_M の1種）から選ぶ。
/// </summary>
public sealed class ModelDownloadService
{
    /// <summary>ダウンロード可能なモデルのカタログ。設定画面の選択肢に出す。
    /// 現在はユーザー向けに <b>Qwen3-4B GGUF Q4_K_M（llama.cpp バックエンド）の1種のみ</b>を提示する。
    /// ONNX は内部的にはなお動くが UI には出さない方針。Q5_K_M は CPU では prefill が約2倍・decode も遅く
    /// 精度の上積みも無かった（`docs/reports/llamacpp-vs-onnx-qwen3-4b.md`）ため提示しない。単体ファイルを
    /// <c>models/&lt;FolderName&gt;/</c> へ置き、ルータが <c>.gguf</c> 拡張子で llama.cpp へ振り分ける。</summary>
    public static readonly IReadOnlyList<DownloadableModel> Catalog = new[]
    {
        new DownloadableModel(
            Id: "qwen3-4b-q4_k_m",
            DisplayName: "Qwen3-4B GGUF Q4_K_M（CPU・約2.5GB・推奨）",
            Repo: "Qwen/Qwen3-4B-GGUF",
            Subfolder: "",
            FolderName: "qwen3-4b-q4_k_m",
            Files: new[] { "Qwen3-4B-Q4_K_M.gguf" }),
    };

    /// <summary>既定で選択するモデル（Qwen3-4B GGUF Q4_K_M）。</summary>
    public static DownloadableModel Default => Catalog[0];

    private readonly HttpClient _http;

    public ModelDownloadService(HttpClient http) => _http = http;

    /// <summary>モデルルート（<c>%APPDATA%/Loomo/models</c>）。設定のモデル一覧もここを走査する。</summary>
    public static string DefaultModelsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "models");

    /// <summary>ダウンロード進捗。</summary>
    public sealed record Progress(long DownloadedBytes, long TotalBytes, string CurrentFile, int FileIndex, int FileCount);

    /// <summary>
    /// 指定モデル一式をダウンロードし、保存先フォルダの絶対パスを返す。進捗は <paramref name="progress"/> に通知。
    /// 途中の失敗・キャンセル時は書きかけのファイルを片付けてから例外/キャンセルを伝播する。
    /// </summary>
    public async Task<string> DownloadAsync(DownloadableModel model, IProgress<Progress>? progress, CancellationToken ct)
    {
        var target = model.TargetDir;
        Directory.CreateDirectory(target);

        // 進捗の総バイト数は HEAD で各ファイルサイズを合算して見積もる（取得不能なら 0 のまま）。
        var sizes = new long[model.Files.Length];
        long total = 0;
        for (var i = 0; i < model.Files.Length; i++)
        {
            sizes[i] = await GetContentLengthAsync(model, model.Files[i], ct);
            if (sizes[i] > 0) total += sizes[i];
        }

        long done = 0;
        for (var i = 0; i < model.Files.Length; i++)
        {
            var file = model.Files[i];
            var dest = Path.Combine(target, file);

            // 既に正しいサイズで存在すればスキップ（再実行時のレジューム）。
            if (sizes[i] > 0 && File.Exists(dest) && new FileInfo(dest).Length == sizes[i])
            {
                done += sizes[i];
                progress?.Report(new Progress(done, total, file, i + 1, model.Files.Length));
                continue;
            }

            progress?.Report(new Progress(done, total, file, i + 1, model.Files.Length));
            done += await DownloadFileAsync(model, file, dest, done, total, i, progress, ct);
        }

        return target;
    }

    private async Task<long> DownloadFileAsync(
        DownloadableModel model, string file, string dest, long baseDone, long total, int index,
        IProgress<Progress>? progress, CancellationToken ct)
    {
        var tmp = dest + ".part";
        // ネストしたファイル名（バリアント内サブパス）にも備えて親フォルダを作る。
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        try
        {
            using var resp = await _http.GetAsync(Url(model, file), HttpCompletionOption.ResponseHeadersRead, ct);
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
                    progress?.Report(new Progress(baseDone + fileDone, total, file, index + 1, model.Files.Length));
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

    private async Task<long> GetContentLengthAsync(DownloadableModel model, string file, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, Url(model, file));
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.IsSuccessStatusCode && resp.Content.Headers.ContentLength is { } len)
                return len;
        }
        catch (OperationCanceledException) { throw; }
        catch { /* サイズ不明（総量見積もりに含めないだけ） */ }
        return 0;
    }

    private static string Url(DownloadableModel model, string file)
    {
        var path = string.IsNullOrEmpty(model.Subfolder) ? file : $"{model.Subfolder}/{file}";
        return $"https://huggingface.co/{model.Repo}/resolve/main/{path}";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 片付け失敗は無視 */ }
    }
}
