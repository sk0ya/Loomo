using System.IO;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 言語サーバーが「ライブラリの定義」として返す、逆コンパイル結果の一時ファイル（設計書 §36）。
///
/// <para>Roslyn は BCL／NuGet の型の定義へ飛ぶと <c>%TEMP%\MetadataAsSource\…\Console.cs</c> を作って
/// その file URI を返す（2026-10-11 実測）。開けるが、中身は逆コンパイル結果で書き換える意味が無く、
/// 保存しても次の起動では消える。だから読み取り専用で開き、出所を状態行に出す。</para>
/// </summary>
public static class MetadataSourceFiles
{
    private const string MetadataFolder = "MetadataAsSource";

    /// <summary><paramref name="path"/> が一時フォルダー配下の MetadataAsSource にあるか。</summary>
    public static bool IsMetadataAsSource(string? path, string? tempRoot = null)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return false; }

        var root = Path.GetFullPath(tempRoot ?? Path.GetTempPath());
        var prefix = Path.Combine(root, MetadataFolder) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>状態行に出す説明。SourceLink／埋め込みソースのときもあるので「ライブラリのソース」と呼ぶ。</summary>
    public static string Describe(string path)
        => $"ライブラリのソース（読み取り専用・逆コンパイルまたは SourceLink）: {Path.GetFileName(path)}";
}
