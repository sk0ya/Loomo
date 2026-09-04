using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;

namespace sk0ya.Loomo.CSharp.Projects;

/// <summary>
/// <see cref="MetadataReference"/> をプロセス全体で共有するキャッシュ。
///
/// <para><b>なぜ要るか</b>——<c>MetadataReference.CreateFromFile</c> は毎回アセンブリのメタデータを
/// 読み直して <c>AssemblyMetadata</c> を作り、それは Compilation が生きている間ずっとメモリに残る。
/// C# 診断（StyleCop／compiler）は<b>入力が止まるたび・ソリューション変更のたび・タブごと</b>に走るので、
/// 都度作り直すとプロジェクトの参照（<c>TRUSTED_PLATFORM_ASSEMBLIES</c> だけで200個近い）が
/// 解析回数ぶん積み上がる。実測では数分の操作でヒープが 1.6GB まで膨らみ、その状態で
/// コンボボックスを開いた瞬間のアロケーションがブロッキング GC を誘発して、UI スレッドが
/// <b>19.5秒</b>止まった（最終的に Windows が AppHang でアプリを落とす）。
/// 同じ DLL には同じ参照を返す——それがこのクラスの唯一の役目である。</para>
///
/// <para>ビルド出力の DLL は解析中に差し替わるので、キーは<b>パスだけでなく更新時刻とサイズ</b>を含める。
/// 古いエントリはそのまま捨てて作り直す（Roslyn の推奨どおり、参照の同一性は入力の同一性で決める）。</para>
/// </summary>
public static class MetadataReferenceCache
{
    private readonly record struct Entry(DateTime WriteTimeUtc, long Length, PortableExecutableReference Reference);

    private static readonly ConcurrentDictionary<string, Entry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// パスに対応する参照を返す。読めない・壊れているアセンブリでは null を返し、
    /// 呼び出し側はその1件だけを飛ばせる（解析全体を落とさない）。
    /// </summary>
    public static PortableExecutableReference? Get(string path)
    {
        string fullPath;
        DateTime writeTime;
        long length;
        try
        {
            fullPath = Path.GetFullPath(path);
            var info = new FileInfo(fullPath);
            if (!info.Exists) return null;
            writeTime = info.LastWriteTimeUtc;
            length = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   ArgumentException or NotSupportedException)
        {
            return null;
        }

        if (Cache.TryGetValue(fullPath, out var cached) &&
            cached.WriteTimeUtc == writeTime && cached.Length == length)
            return cached.Reference;

        PortableExecutableReference reference;
        try
        {
            // <c>MetadataReference.CreateFromFile</c> は DLL をメモリマップしたまま
            // <c>AssemblyMetadata</c> の生存期間ずっと掴む（共有は読み取りと削除だけ）。
            // ここはプロセスの寿命までキャッシュするので、それだと利用者自身の
            // <c>bin/**/*.dll</c> が書き込みロックされ、次の <c>dotnet build</c> が
            // 出力コピーで共有違反になる——メタデータだけ先読みして<b>ファイルは閉じる</b>。
            // （ロックしたままだと、上の更新時刻チェックも永久に発火しなくなる。）
            using var stream = File.OpenRead(fullPath);
            reference = AssemblyMetadata
                .CreateFromStream(stream, PEStreamOptions.PrefetchMetadata)
                .GetReference(filePath: fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   ArgumentException or BadImageFormatException)
        {
            return null;
        }

        Cache[fullPath] = new Entry(writeTime, length, reference);
        return reference;
    }

    /// <summary>キャッシュを空にする（テスト用。参照そのものは GC に任せる）。</summary>
    public static void Clear() => Cache.Clear();

    /// <summary>キャッシュしている参照の数。</summary>
    public static int Count => Cache.Count;
}
