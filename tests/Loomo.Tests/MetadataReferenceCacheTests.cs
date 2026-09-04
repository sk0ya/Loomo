using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 参照アセンブリを解析のたびに作り直さないこと。作り直すと <c>AssemblyMetadata</c> が
/// 解析回数ぶん積み上がり、ヒープが GB 級に膨らんでブロッキング GC で UI が数十秒止まる
/// （実測19.5秒／最終的に Windows が AppHang でアプリを終了する）ため、
/// 「同じ DLL には同じ参照インスタンス」を不変条件として固定する。
/// </summary>
public sealed class MetadataReferenceCacheTests
{
    [Fact]
    public void 同じアセンブリには同じ参照インスタンスを返す()
    {
        var path = typeof(object).Assembly.Location;

        var first = MetadataReferenceCache.Get(path);
        var second = MetadataReferenceCache.Get(path);

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void パスの表記が違っても同じ参照を返す()
    {
        var path = typeof(object).Assembly.Location;
        var directory = Path.GetDirectoryName(path)!;
        var roundabout = Path.Combine(directory, ".", Path.GetFileName(path));

        Assert.Same(MetadataReferenceCache.Get(path), MetadataReferenceCache.Get(roundabout));
    }

    [Fact]
    public void 参照を作ってもDLLを書き込みロックしない()
    {
        // プロセスの寿命までキャッシュするので、DLL を掴んだままだと利用者自身の bin\*.dll が
        // ロックされ、次の dotnet build が出力コピーで共有違反になる。
        var source = typeof(object).Assembly.Location;
        var copy = Path.Combine(Path.GetTempPath(), $"loomo-lock-{Guid.NewGuid():N}.dll");
        File.Copy(source, copy);
        try
        {
            Assert.NotNull(MetadataReferenceCache.Get(copy));

            // ビルドの出力コピーと同じこと（上書き）ができる＝掴んでいない。
            File.Copy(source, copy, overwrite: true);
            File.Delete(copy);
            Assert.False(File.Exists(copy));
        }
        finally
        {
            if (File.Exists(copy)) File.Delete(copy);
        }
    }

    [Fact]
    public void 読めないパスは解析を落とさずnullを返す()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"loomo-missing-{Guid.NewGuid():N}.dll");

        Assert.Null(MetadataReferenceCache.Get(missing));
    }

    [Fact]
    public void Compilationを作り直しても参照は使い回される()
    {
        var sources = new Dictionary<string, string> { ["A.cs"] = "class A { }" };

        var first = CSharpSemanticCompilation.Create(sources);
        var second = CSharpSemanticCompilation.Create(sources);

        Assert.NotEmpty(first.References);
        var reused = new HashSet<MetadataReference>(first.References, ReferenceEqualityComparer.Instance);
        Assert.All(second.References, reference => Assert.Contains(reference, reused));
    }
}
