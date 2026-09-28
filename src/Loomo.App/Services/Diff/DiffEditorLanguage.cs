using System.Collections.Concurrent;
using System.IO;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 差分のエディタへ読み取り専用の文書として載せるときの言語名。ファイルとして開かないので
/// エディタは拡張子から言語を決められない——同じ判定（<see cref="EditorSyntaxColors.CreateEngine"/>＝
/// エディタペインと同じ構文の登録）で名前を引いて渡す。拡張子ごとに覚えておく。
/// </summary>
internal static class DiffEditorLanguage
{
    private static readonly ConcurrentDictionary<string, string?> ByExtension = new(StringComparer.OrdinalIgnoreCase);

    internal static string? For(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var extension = Path.GetExtension(path);
        // 拡張子の無いファイル（Makefile・Dockerfile）は名前で決まるので、名前ごと覚える。
        var key = extension.Length > 0 ? extension : Path.GetFileName(path);
        return ByExtension.GetOrAdd(key, _ => EditorSyntaxColors.CreateEngine(path)?.LanguageName);
    }
}
