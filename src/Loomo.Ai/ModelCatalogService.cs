using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Ai;

/// <summary>
/// ローカルに配置済みの ONNX モデルフォルダ（<c>genai_config.json</c> を含むもの）を列挙する。
/// 既定のモデルルート <c>%APPDATA%/Loomo/models</c> の直下と、現在設定中のモデルフォルダの親を走査し、
/// 設定画面のモデル選択肢として提示する。HTTP（旧 Ollama <c>/api/tags</c>）には依存しない。
/// </summary>
public sealed class ModelCatalogService
{
    private readonly AiSettings _settings;

    public ModelCatalogService(AiSettings settings) => _settings = settings;

    /// <summary>モデル一覧取得に対応するプロバイダか。</summary>
    public static bool Supports(AiProvider provider) => provider is AiProvider.Local;

    /// <summary>
    /// 利用可能なローカル ONNX モデルフォルダ名の一覧を返す（重複排除・名前順、phi4-mini を先頭に）。
    /// 表示名はフォルダ名。実フォルダは <see cref="ResolvePath"/> で復元できる。
    /// </summary>
    public Task<IReadOnlyList<string>> FetchAsync(AiProvider provider, CancellationToken ct = default)
    {
        if (!Supports(provider))
            throw new NotSupportedException($"{provider} はモデル一覧取得に対応していません。");

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in EnumerateModelDirs())
        {
            var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrEmpty(name) && seen.Add(name))
                names.Add(name);
        }

        IReadOnlyList<string> result = names
            .OrderBy(Phi4MiniRank)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult(result);
    }

    /// <summary>モデルフォルダ名から実フォルダの絶対パスを復元する。
    /// モデルルート直下を優先し、無ければ現在のモデルフォルダの親を見る。見つからなければ空文字を返す。</summary>
    public string ResolvePath(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return "";

        foreach (var root in Roots())
        {
            var candidate = Path.Combine(root, modelName);
            if (IsOnnxModelDir(candidate)) return candidate;
        }
        return "";
    }

    private IEnumerable<string> EnumerateModelDirs()
    {
        foreach (var root in Roots())
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> subdirs;
            try { subdirs = Directory.EnumerateDirectories(root); }
            catch { continue; }
            foreach (var d in subdirs)
                if (IsOnnxModelDir(d))
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

    private static int Phi4MiniRank(string name) =>
        name.Contains("phi", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
}
