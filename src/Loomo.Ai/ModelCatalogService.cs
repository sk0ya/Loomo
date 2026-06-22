using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Ai;

/// <summary>
/// ローカルに配置済みのモデルフォルダを列挙する。対象は (1) ONNX モデルフォルダ（<c>genai_config.json</c> を
/// 含む）と (2) GGUF モデルフォルダ（<c>*.gguf</c> を含む・llama.cpp バックエンド用）。既定のモデルルート
/// <c>%APPDATA%/Loomo/models</c> の直下と、現在設定中のモデルの親を走査し、設定画面のモデル選択肢として提示する。
/// HTTP（旧 Ollama <c>/api/tags</c>）には依存しない。GGUF は <see cref="ResolvePath"/> が <c>.gguf</c> ファイル
/// パスを返し、ルータ（<see cref="Clients.LocalInferenceRouter"/>）が拡張子で llama.cpp へ振り分ける。
/// </summary>
public sealed class ModelCatalogService
{
    private readonly AiSettings _settings;

    public ModelCatalogService(AiSettings settings) => _settings = settings;

    /// <summary>モデル一覧取得に対応するプロバイダか。</summary>
    public static bool Supports(AiProvider provider) => provider is AiProvider.Local;

    /// <summary>
    /// 設定画面のモデル選択肢として、ローカルの <b>GGUF モデルフォルダ名のみ</b>を返す（重複排除・名前順）。
    /// ONNX フォルダはユーザーには出さない方針のため一覧には含めない（ただし <see cref="ResolvePath"/> は
    /// ONNX も解決するので、手入力でフォルダ名を指定すれば従来どおり使える）。実パスは <see cref="ResolvePath"/>
    /// で復元する（GGUF は <c>.gguf</c> ファイルパス）。
    /// </summary>
    public Task<IReadOnlyList<string>> FetchAsync(AiProvider provider, CancellationToken ct = default)
    {
        if (!Supports(provider))
            throw new NotSupportedException($"{provider} はモデル一覧取得に対応していません。");

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in EnumerateGgufModelDirs())
        {
            var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrEmpty(name) && seen.Add(name))
                names.Add(name);
        }

        IReadOnlyList<string> result = names
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult(result);
    }

    /// <summary>モデルフォルダ名から実パスを復元する。ONNX はフォルダパス、GGUF はフォルダ内の
    /// <c>.gguf</c> ファイルパスを返す（ルータが拡張子でバックエンドを振り分ける）。
    /// モデルルート直下を優先し、無ければ現在のモデルの親を見る。見つからなければ空文字を返す。</summary>
    public string ResolvePath(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return "";

        foreach (var root in Roots())
        {
            var candidate = Path.Combine(root, modelName);
            if (IsOnnxModelDir(candidate)) return candidate;
            if (GgufFileIn(candidate) is { } gguf) return gguf;
        }
        return "";
    }

    /// <summary>ユーザー向け一覧用：GGUF を含むフォルダのみ列挙する（ONNX フォルダは出さない）。</summary>
    private IEnumerable<string> EnumerateGgufModelDirs()
    {
        foreach (var root in Roots())
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> subdirs;
            try { subdirs = Directory.EnumerateDirectories(root); }
            catch { continue; }
            foreach (var d in subdirs)
                if (GgufFileIn(d) is not null)
                    yield return d;
        }
    }

    /// <summary>走査対象のルート群（既定モデルルート＋現在のモデルフォルダの親）。</summary>
    private IEnumerable<string> Roots()
    {
        yield return ModelDownloadService.DefaultModelsRoot;

        var current = _settings.Local.ModelPath;
        if (!string.IsNullOrWhiteSpace(current))
        {
            var parent = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(parent))
                yield return parent;
        }
    }

    private static bool IsOnnxModelDir(string dir) =>
        Directory.Exists(dir) && File.Exists(Path.Combine(dir, "genai_config.json"));

    /// <summary>フォルダ内の最初の <c>.gguf</c> ファイルの絶対パス（無ければ null）。GGUF モデルフォルダの判定と
    /// パス復元に使う。</summary>
    private static string? GgufFileIn(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        try { return Directory.EnumerateFiles(dir, "*.gguf").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault(); }
        catch { return null; }
    }
}
